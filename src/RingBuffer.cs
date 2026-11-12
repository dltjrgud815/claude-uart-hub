// 케이블별 RX 링 버퍼(기본 32MB). 절대 offset(epoch:offset)과 청크 도착 시각을 기록하고,
// 구독자(wait --since, telnet fanout, PromptTracker)에게 읽기 전용 커서를 준다.
// writer는 PortReader 하나뿐이다. 구독자가 느려 덮어써진 구간은 Lost 로 알린다(느린 소비자 끊기, P1).
using System;
using System.Globalization;
using System.Threading;

namespace UartHub
{
    public enum RingReadStatus { Data, Timeout, Lost, Closed }

    public struct RingRead
    {
        public RingReadStatus Status;
        public long Offset;        // Data: 첫 바이트의 절대 offset / Lost: 건너뛴 뒤 새 위치
        public byte[] Data;
        public long MonoMs;        // 청크 도착 시각(메타가 없으면 -1)
        public DateTime Wall;
        public long Skipped;       // Lost 일 때 잃은 바이트 수
    }

    public sealed class RingBuffer
    {
        readonly object sync = new object();
        readonly byte[] buf;
        readonly int cap;
        long end;
        bool closed;

        readonly long[] mOff, mMono, mWall;
        readonly int mCap;
        long mCount;

        public long Epoch { get; }

        public RingBuffer(long capacityBytes, int metaSlots, long epoch)
        {
            if (capacityBytes < 1024) capacityBytes = 1024;
            if (capacityBytes > int.MaxValue / 2) capacityBytes = int.MaxValue / 2;
            cap = (int)capacityBytes;
            buf = new byte[cap];
            mCap = Math.Max(16, metaSlots);
            mOff = new long[mCap]; mMono = new long[mCap]; mWall = new long[mCap];
            Epoch = epoch;
        }

        /// <summary>다음 바이트가 받을 절대 offset(= 지금까지 받은 총 바이트).</summary>
        public long End { get { lock (sync) return end; } }
        /// <summary>아직 링에 남아 있는 가장 오래된 offset.</summary>
        public long Start { get { lock (sync) return Math.Max(0, end - cap); } }

        public string Format(long abs) => Epoch.ToString(CultureInfo.InvariantCulture) + ":" + abs.ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string s, out long epoch, out long offset)
        {
            epoch = 0; offset = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int i = s.IndexOf(':');
            if (i <= 0) return false;
            return long.TryParse(s.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch)
                && long.TryParse(s.Substring(i + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
        }

        /// <summary>PortReader 전용. 메모리 복사만 한다(디스크·정규식 없음).</summary>
        public void Append(byte[] data, int off, int len, long mono, DateTime wall)
        {
            if (len <= 0) return;
            lock (sync)
            {
                long k = mCount % mCap;
                mOff[k] = end; mMono[k] = mono; mWall[k] = wall.Ticks;
                mCount++;

                int srcOff = off, n = len;
                long dstAbs = end;
                if (n > cap) { srcOff += n - cap; dstAbs += n - cap; n = cap; }
                int pos = (int)(dstAbs % cap);
                int first = Math.Min(n, cap - pos);
                Buffer.BlockCopy(data, srcOff, buf, pos, first);
                if (n > first) Buffer.BlockCopy(data, srcOff + first, buf, 0, n - first);
                end += len;
                Monitor.PulseAll(sync);
            }
        }

        public void Close()
        {
            lock (sync) { closed = true; Monitor.PulseAll(sync); }
        }

        /// <summary>from 부터 최대 max 바이트 복사. from 이 이미 덮어써졌으면 -1.</summary>
        public int Read(long from, byte[] dst, int dstOff, int max)
        {
            lock (sync) return ReadLocked(from, dst, dstOff, max);
        }

        int ReadLocked(long from, byte[] dst, int dstOff, int max)
        {
            long start = Math.Max(0, end - cap);
            if (from < start) return -1;
            if (from >= end) return 0;
            int n = (int)Math.Min(max, end - from);
            int pos = (int)(from % cap);
            int first = Math.Min(n, cap - pos);
            Buffer.BlockCopy(buf, pos, dst, dstOff, first);
            if (n > first) Buffer.BlockCopy(buf, 0, dst, dstOff + first, n - first);
            return n;
        }

        /// <summary>abs 를 포함하는 청크의 도착 시각. 메타가 덮어써졌으면 false.</summary>
        public bool TryGetArrival(long abs, out long mono, out DateTime wall)
        {
            lock (sync)
            {
                long k = FindMeta(abs);
                if (k < 0) { mono = -1; wall = default; return false; }
                int i = (int)(k % mCap);
                mono = mMono[i]; wall = new DateTime(mWall[i]);
                return true;
            }
        }

        // abs 이하의 가장 큰 청크 시작을 가진 메타의 논리 인덱스(없으면 -1)
        long FindMeta(long abs)
        {
            long lo = Math.Max(0, mCount - mCap), hi = mCount - 1, ans = -1;
            while (lo <= hi)
            {
                long mid = lo + (hi - lo) / 2;
                if (mOff[mid % mCap] <= abs) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return ans;
        }

        public RingCursor OpenCursor(long from) => new RingCursor(this, from);

        internal RingRead NextFor(ref long pos, int timeoutMs, int maxBytes)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            lock (sync)
            {
                while (true)
                {
                    long start = Math.Max(0, end - cap);
                    if (pos < start)
                    {
                        var lost = new RingRead { Status = RingReadStatus.Lost, Skipped = start - pos, Offset = start, MonoMs = -1 };
                        pos = start;
                        return lost;
                    }
                    if (pos < end) break;
                    if (closed) return new RingRead { Status = RingReadStatus.Closed, Offset = pos, MonoMs = -1 };
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) return new RingRead { Status = RingReadStatus.Timeout, Offset = pos, MonoMs = -1 };
                    Monitor.Wait(sync, (int)Math.Min(left, int.MaxValue));
                }
                long limit = end;
                long mono = -1; DateTime wall = default;
                long k = FindMeta(pos);
                if (k >= 0)
                {
                    int i = (int)(k % mCap);
                    mono = mMono[i]; wall = new DateTime(mWall[i]);
                    if (k + 1 < mCount) limit = Math.Min(limit, mOff[(k + 1) % mCap]);
                }
                int n = (int)Math.Min(Math.Min(limit - pos, maxBytes), int.MaxValue);
                var data = new byte[n];
                ReadLocked(pos, data, 0, n);
                var r = new RingRead { Status = RingReadStatus.Data, Offset = pos, Data = data, MonoMs = mono, Wall = wall };
                pos += n;
                return r;
            }
        }
    }

    /// <summary>단일 소비자용 읽기 커서. Next 는 청크 경계까지(도착 시각이 하나인 구간) 돌려준다.</summary>
    public sealed class RingCursor
    {
        readonly RingBuffer ring;
        long pos;
        internal RingCursor(RingBuffer ring, long from) { this.ring = ring; pos = Math.Max(0, from); }
        public long Position => pos;
        public RingRead Next(int timeoutMs, int maxBytes = 65536) => ring.NextFor(ref pos, timeoutMs, maxBytes);
    }
}
