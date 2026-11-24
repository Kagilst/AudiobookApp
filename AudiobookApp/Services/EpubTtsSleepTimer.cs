using System;

namespace AudiobookApp.Services
{
    /// <summary>
    /// Minute-based sleep countdown for EPUB TTS (testable without UI).
    /// </summary>
    public sealed class EpubTtsSleepTimer
    {
        private DateTimeOffset? _deadlineUtc;

        public bool IsActive => _deadlineUtc.HasValue;

        public void StartMinutes(int minutes, DateTimeOffset? nowUtc = null)
        {
            if (minutes <= 0)
            {
                Cancel();
                return;
            }

            var now = nowUtc ?? DateTimeOffset.UtcNow;
            _deadlineUtc = now.AddMinutes(minutes);
        }

        public void Cancel()
        {
            _deadlineUtc = null;
        }

        public TimeSpan GetRemaining(DateTimeOffset? nowUtc = null)
        {
            if (_deadlineUtc is not DateTimeOffset deadline)
                return TimeSpan.Zero;

            var remaining = deadline - (nowUtc ?? DateTimeOffset.UtcNow);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public bool HasExpired(DateTimeOffset? nowUtc = null)
        {
            if (_deadlineUtc is not DateTimeOffset deadline)
                return false;

            return (nowUtc ?? DateTimeOffset.UtcNow) >= deadline;
        }

        public string FormatRemaining(DateTimeOffset? nowUtc = null)
        {
            if (!IsActive)
                return string.Empty;

            var remaining = GetRemaining(nowUtc);
            return remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss")
                : remaining.ToString(@"mm\:ss");
        }
    }
}
