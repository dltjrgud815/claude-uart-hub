// 케이블별 수신 스레드(§3.1). Read → 도착 시각 부여 → 링(메모리) + 큐(메모리)만 한다.
// 디스크·정규식·fanout 은 하지 않는다(P1). Read 예외는 타입과 무관하게 detach 로 처리한다(S0-6).
using System;
using System.Threading;

namespace UartHub
{
    internal sealed class PortReader
    {
        readonly IPort port;
        readonly IClock clock;
        readonly Action<PortReader, Exception> onFail;
        readonly int bufBytes;
        Thread thread;
        volatile bool stop;

        public Cable Cable { get; }

        public PortReader(Cable cable, IPort port, IClock clock, int bufBytes, Action<PortReader, Exception> onFail)
        {
            Cable = cable; this.port = port; this.clock = clock; this.bufBytes = bufBytes; this.onFail = onFail;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "PortReader-" + Cable.Id };
            thread.Start();
        }

        public void RequestStop() => stop = true;
        public bool Join(int ms) => thread == null || thread.Join(ms);

        void Run()
        {
            var buf = new byte[bufBytes];
            while (!stop)
            {
                int n;
                try { n = port.Read(buf, 0, buf.Length); }
                catch (Exception e)
                {
                    if (!stop) onFail(this, e);
                    return;
                }
                if (n <= 0) continue;
                try
                {
                    long mono = clock.MonoMs;
                    DateTime wall = clock.Now;
                    var ring = Cable.Ring;
                    var queue = Cable.Queue;
                    long abs = ring.End;
                    ring.Append(buf, 0, n, mono, wall);
                    var data = new byte[n];
                    Buffer.BlockCopy(buf, 0, data, 0, n);
                    if (!queue.TryEnqueueData(new QItem { Kind = QKind.Data, Abs = abs, Data = data, Mono = mono, Wall = wall }))
                        queue.AddDrop(abs, n, mono, wall);
                    Cable.AddRx(n);
                }
                catch (Exception e)
                {
                    // 메모리 적재 실패(버그·메모리 부족): 조용히 죽지 않고 detach 로 알려 다음 스캔에서 다시 연결한다.
                    if (!stop) onFail(this, e);
                    return;
                }
            }
        }
    }
}
