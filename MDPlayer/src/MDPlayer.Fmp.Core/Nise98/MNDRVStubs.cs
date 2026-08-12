// Real FMTimer implementation ported from MDPlayerx64/Driver/MNDRV/reg.cs
// This replaces the no-op stub. The timer advances counters and sets
// overflow flags so that Nise98.IntTimer() returns true when a timer
// expires, triggering the FMP interrupt handler.
using Fmp.Core.Nise98;

namespace MNDRV
{
    public class FMTimer
    {
        private int TimerAregH;        // Timer A high 8 bits
        private int TimerAregL;        // Timer A low 2 bits
        private int TimerA;            // Timer A overflow threshold
        private double TimerAcounter;  // Timer A counter
        private int TimerB;            // Timer B overflow threshold
        private double TimerBcounter;  // Timer B counter
        private int TimerReg;          // Timer control register
        private int StatReg;           // Status register (lower 2 bits)
        private bool isOPM = false;
        private Action CsmKeyOn;
        private double step = 0.0;
        private double MasterClock = 3579545.0;

        public FMTimer(bool isOPM, Action CsmKeyOn, double MasterClock, double sampleRate = 44100.0)
        {
            if (!double.IsFinite(sampleRate) || sampleRate <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "sample rate must be positive");

            this.isOPM = isOPM;
            this.CsmKeyOn = CsmKeyOn;
            this.MasterClock = MasterClock;
            if (isOPM)
            {
                step = MasterClock / 64.0 / 1.0 / sampleRate;
            }
            else
            {
                step = MasterClock / 72.0 / 2.0 / sampleRate;
            }
        }

        public OpnaTimer InnerTimer => null; // Not used; real implementation

        public void timer()
        {
            int flag_set = 0;

            if ((TimerReg & 0x01) != 0)
            {   // TimerA running
                TimerAcounter += step;
                if (TimerAcounter >= TimerA)
                {
                    flag_set |= ((TimerReg >> 2) & 0x01);
                    TimerAcounter -= TimerA;
                    if ((TimerReg & 0x80) != 0) CsmKeyOn?.Invoke();
                }
            }

            if ((TimerReg & 0x02) != 0)
            {   // TimerB running
                TimerBcounter += step;
                if (TimerBcounter >= TimerB)
                {
                    flag_set |= ((TimerReg >> 2) & 0x02);
                    TimerBcounter -= TimerB;
                }
            }

            StatReg |= flag_set;
        }

        public void Start() { }
        public void Stop() { }

        public void WriteReg(int adr, int data)
        {
            if (isOPM) WriteRegOPM((byte)adr, (byte)data);
            else WriteRegOPN((byte)adr, (byte)data);
        }

        private void WriteRegOPM(byte adr, byte data)
        {
            switch (adr)
            {
                case 0x10:
                case 0x11:
                    if (adr == 0x10) TimerAregH = data;
                    else TimerAregL = data & 3;
                    TimerA = 1024 - ((TimerAregH << 2) + TimerAregL);
                    break;

                case 0x12:
                    TimerB = (256 - (int)data) << (10 - 6);
                    break;

                case 0x14:
                    TimerReg = data & 0x8F;
                    StatReg &= 0xFF - ((data >> 4) & 3);
                    break;
            }
        }

        private void WriteRegOPN(byte adr, byte data)
        {
            switch (adr)
            {
                case 0x24:
                case 0x25:
                    if (adr == 0x24) TimerAregH = data;
                    else TimerAregL = data & 3;
                    TimerA = 1024 - ((TimerAregH << 2) + TimerAregL);
                    break;

                case 0x26:
                    TimerB = (256 - (int)data) << (10 - 6);
                    break;

                case 0x27:
                    TimerReg = data & 0x8F;
                    StatReg &= 0xFF - ((data >> 4) & 3);
                    break;
            }
        }

        public int ReadStatus()
        {
            return StatReg;
        }
    }
}
