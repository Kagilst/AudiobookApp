using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace AudiobookApp.Services
{
    /// <summary>
    /// Full-book EPUB text-to-speech using Windows voices.
    /// </summary>
    public sealed class EpubTtsService : IDisposable
    {
        private readonly SpeechSynthesizer _synthesizer = new();
        private readonly MediaPlayer _player = new() { AutoPlay = false };
        private CancellationTokenSource? _cts;
        private bool _paused;
        private bool _disposed;

        public bool IsPlaying { get; private set; }
        public bool IsPaused => _paused;
        public int ChapterIndex { get; private set; }
        public int CharOffset { get; private set; }

        /// <summary>Playback volume from 0.0 to 1.0.</summary>
        public double Volume
        {
            get => _player.Volume;
            set => _player.Volume = EpubTextChunker.ClampVolume(value);
        }

        /// <summary>Speaking rate from 0.5 to 6.0 (1.0 is normal).</summary>
        public double SpeakingRate
        {
            get => _synthesizer.Options.SpeakingRate;
            set
            {
                var rate = EpubTextChunker.ClampSpeakingRate(value);
                _synthesizer.Options.SpeakingRate = rate;
                _player.PlaybackRate = rate;
            }
        }

        public event Action? StateChanged;
        /// <summary>Chapter index, chunk start offset, chunk end offset (exclusive).</summary>
        public event Action<int, int, int>? PositionChanged;
        public event Action? Completed;

        public EpubTtsService()
        {
        }

        public IReadOnlyList<VoiceInformation> GetVoices() =>
            SpeechSynthesizer.AllVoices.OrderBy(v => v.DisplayName).ToList();

        public VoiceInformation? CurrentVoice => _synthesizer.Voice;

        public void SetVoice(VoiceInformation voice)
        {
            if (voice != null)
                _synthesizer.Voice = voice;
        }

        public async Task StartAsync(
            IReadOnlyList<(int ChapterIndex, string PlainText)> chapters,
            int startChapterIndex,
            int startCharOffset)
        {
            StopInternal(raiseState: false);
            _cts = new CancellationTokenSource();
            _paused = false;
            IsPlaying = true;
            StateChanged?.Invoke();

            try
            {
                for (var i = Math.Clamp(startChapterIndex, 0, Math.Max(chapters.Count - 1, 0));
                     i < chapters.Count;
                     i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    ChapterIndex = i;
                    var text = chapters[i].PlainText ?? string.Empty;
                    var offset = i == startChapterIndex
                        ? Math.Clamp(startCharOffset, 0, text.Length)
                        : 0;

                    await SpeakChapterAsync(text, offset, _cts.Token);
                    startCharOffset = 0;
                }

                IsPlaying = false;
                _paused = false;
                Completed?.Invoke();
                StateChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                IsPlaying = false;
                StateChanged?.Invoke();
            }
            catch
            {
                IsPlaying = false;
                _paused = false;
                StateChanged?.Invoke();
            }
        }

        public void Pause()
        {
            if (!IsPlaying || _paused)
                return;

            _paused = true;
            _player.Pause();
            StateChanged?.Invoke();
        }

        public void Resume()
        {
            if (!IsPlaying || !_paused)
                return;

            _paused = false;
            _player.Play();
            StateChanged?.Invoke();
        }

        public void Stop()
        {
            StopInternal(raiseState: true);
        }

        private void StopInternal(bool raiseState)
        {
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            _cts = null;
            _player.Pause();
            _player.Source = null;
            IsPlaying = false;
            _paused = false;
            if (raiseState)
                StateChanged?.Invoke();
        }

        private async Task SpeakChapterAsync(string text, int startOffset, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(text) || startOffset >= text.Length)
                return;

            var chunks = EpubTextChunker.SplitIntoChunks(text, startOffset);
            foreach (var (offset, chunk) in chunks)
            {
                token.ThrowIfCancellationRequested();
                CharOffset = offset;
                var chunkEnd = offset + chunk.Length;
                PositionChanged?.Invoke(ChapterIndex, offset, chunkEnd);

                using var stream = await _synthesizer.SynthesizeTextToStreamAsync(chunk);
                token.ThrowIfCancellationRequested();

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnEnded(MediaPlayer _, object __) => tcs.TrySetResult();

                _player.MediaEnded += OnEnded;
                try
                {
                    _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                    if (_paused)
                    {
                        // Wait until resumed or cancelled.
                        while (_paused && !token.IsCancellationRequested)
                            await Task.Delay(100, token);
                    }

                    token.ThrowIfCancellationRequested();
                    _player.Play();
                    await using (token.Register(() => tcs.TrySetCanceled(token)))
                        await tcs.Task;
                }
                finally
                {
                    _player.MediaEnded -= OnEnded;
                }

                CharOffset = chunkEnd;
                PositionChanged?.Invoke(ChapterIndex, chunkEnd, chunkEnd);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            StopInternal(raiseState: false);
            _synthesizer.Dispose();
        }
    }
}
