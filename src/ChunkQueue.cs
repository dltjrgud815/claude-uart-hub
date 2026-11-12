// PortReader → DiskWriter 메모리 큐. 데이터 바이트 상한(기본 256MB)을 넘으면 reader가 버리고
// 손실량을 Drop 표시로 남긴다(§3.1). 제어 항목(attach/detach/이벤트/stop)은 상한과 무관하게 들어간다.
using System;
using System.Collections.Generic;
using System.Threading;

namespace UartHub
{
    internal enum QKind { Data, Drop, Attach, Detach, Event, Stop }

    internal sealed class QItem
    {
        public QKind Kind;
        public long Abs;            // Data/Drop: 첫 바이트 절대 offset
        public byte[] Data;
        public long Mono;
        public DateTime Wall;
        public long Count;          // Drop: 버린 바이트 수
        public string Text;         // Event 줄 / Attach·Detach 사유
        public string Com;
        public long Seq;            // Attach: Cable.AttachSeq
    }

    internal sealed class ChunkQueue
    {
        readonly object sync = new object();
        readonly Queue<QItem> q = new Queue<QItem>();
        readonly long cap;
        long bytes;
        QItem tailDrop;             // 큐 끝이 아직 소비되지 않은 Drop 이면 그 항목(연속 drop 병합)
        long droppedTotal;
        bool inFlight;              // writer 가 꺼낸 항목을 아직 flush 하지 않음

        public ChunkQueue(long capBytes) { cap = capBytes; }

        public long Bytes { get { lock (sync) return bytes; } }
        public long DroppedTotal { get { lock (sync) return droppedTotal; } }

        /// <summary>큐가 비었고 꺼낸 항목도 모두 기록·flush 됨.</summary>
        public bool IsDrained { get { lock (sync) return q.Count == 0 && !inFlight; } }

        /// <summary>writer 가 빈 큐를 확인하고 flush 를 마친 뒤 호출.</summary>
        public void MarkFlushed() { lock (sync) inFlight = false; }

        public bool TryEnqueueData(QItem it)
        {
            lock (sync)
            {
                if (bytes + it.Data.Length > cap) return false;
                q.Enqueue(it);
                bytes += it.Data.Length;
                tailDrop = null;
                Monitor.Pulse(sync);
                return true;
            }
        }

        public void AddDrop(long abs, int n, long mono, DateTime wall)
        {
            lock (sync)
            {
                if (tailDrop != null) tailDrop.Count += n;
                else
                {
                    tailDrop = new QItem { Kind = QKind.Drop, Abs = abs, Count = n, Mono = mono, Wall = wall };
                    q.Enqueue(tailDrop);
                }
                droppedTotal += n;
                Monitor.Pulse(sync);
            }
        }

        public void EnqueueControl(QItem it)
        {
            lock (sync)
            {
                q.Enqueue(it);
                tailDrop = null;
                Monitor.Pulse(sync);
            }
        }

        public QItem Dequeue(int timeoutMs)
        {
            lock (sync)
            {
                if (q.Count == 0) Monitor.Wait(sync, timeoutMs);
                if (q.Count == 0) return null;
                var it = q.Dequeue();
                inFlight = true;
                if (ReferenceEquals(it, tailDrop)) tailDrop = null;
                if (it.Kind == QKind.Data) bytes -= it.Data.Length;
                return it;
            }
        }
    }
}
