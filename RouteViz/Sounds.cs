using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RouteViz
{
    /// <summary>
    /// Click sounds, synthesised in code and played through Windows' PlaySound: this game build strips Unity's
    /// AudioClip.Create/AudioSource.PlayOneShot, so Unity audio can't play our own clips. Not tied to the game's volume.
    /// </summary>
    internal sealed class Sounds : IDisposable
    {
        public enum Kind { Ding, Dong, Error }

        private const int SampleRate = 22050;
        private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004, SndNoStop = 0x0010;

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        private readonly IntPtr[] _wavs = new IntPtr[3];
        private float _volume = -1f;

        /// <summary>Volume 0..1; 0 mutes. Regenerates the clips when it changes.</summary>
        public void SetVolume(float volume)
        {
            volume = Math.Max(0f, Math.Min(1f, volume));
            if (Math.Abs(volume - _volume) < 1e-4f)
                return;
            Stop();
            Free();
            _volume = volume;
            if (volume <= 0f)
                return;
            _wavs[(int)Kind.Ding] = ToHGlobal(Wav(Bell(1046.5, 0.4, 0.12), volume));
            _wavs[(int)Kind.Dong] = ToHGlobal(Wav(Bell(784.0, 0.45, 0.14), volume));
            _wavs[(int)Kind.Error] = ToHGlobal(Wav(Buzz(0.2), volume));
        }

        public void Play(Kind kind)
        {
            IntPtr wav = _wavs[(int)kind];
            if (wav != IntPtr.Zero)
                PlaySound(wav, IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
        }

        public void Dispose()
        {
            Stop();
            Free();
        }

        private static void Stop()
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        }

        private void Free()
        {
            for (int i = 0; i < _wavs.Length; i++)
            {
                if (_wavs[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(_wavs[i]);
                _wavs[i] = IntPtr.Zero;
            }
        }

        // ---------------------------------------------------------------- synthesis (samples in -1..1)

        /// <summary>A soft bell: fundamental plus a few quieter partials, quick attack, exponential decay.</summary>
        private static float[] Bell(double freq, double seconds, double decay)
        {
            int n = (int)(seconds * SampleRate);
            var s = new float[n];
            double[] partials = { 1.0, 2.0, 3.01, 4.2 };
            double[] gains = { 1.0, 0.18, 0.05, 0.0 };
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                double v = 0;
                for (int p = 0; p < partials.Length; p++)
                    v += gains[p] * Math.Sin(2 * Math.PI * freq * partials[p] * t) * Math.Exp(-t / (decay / (1 + p * 0.6)));
                double attack = Math.Min(1.0, t / 0.012);
                double tail = Math.Min(1.0, (seconds - t) / 0.03); // avoid a click at the end
                s[i] = (float)(v / 1.23 * attack * tail);
            }
            return s;
        }

        /// <summary>A gentle low "brr": a rounded tone with a light tremolo.</summary>
        private static float[] Buzz(double seconds)
        {
            int n = (int)(seconds * SampleRate);
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                double v = 0;
                for (int h = 1; h <= 3; h += 2)
                    v += Math.Sin(2 * Math.PI * 150 * h * t) / (h * h); // soft, rounded tone rather than a square buzz
                double tremolo = 0.7 + 0.3 * Math.Sin(2 * Math.PI * 22 * t);
                double env = Math.Min(1.0, t / 0.02) * Math.Min(1.0, (seconds - t) / 0.06);
                s[i] = (float)(v * 0.6 * tremolo * env);
            }
            return s;
        }

        private static byte[] Wav(float[] samples, float volume)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataBytes = samples.Length * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);              // PCM
                w.Write((short)1);              // mono
                w.Write(SampleRate);
                w.Write(SampleRate * 2);        // bytes per second
                w.Write((short)2);              // block align
                w.Write((short)16);             // bits per sample
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataBytes);
                float gain = 0.3f * volume * short.MaxValue; // the game's UI cues are quiet; sit under them
                foreach (float v in samples)
                    w.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v * gain)));
                w.Flush();
                return ms.ToArray();
            }
        }

        private static IntPtr ToHGlobal(byte[] bytes)
        {
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }
    }
}
