using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SubtitleVideoPlayerWpf
//namespace LangRepeater
{
    public partial class MainWindow : Window
    {
        // Configuration settings
        private const int CfgStartSegment = 1; // start from 1
        private const int CfgDelayMs = 2000;
        private const int CfgRepetitions = 3;
        private int _subtitleExtraDurationMs = 0;

        private const string WindowTitle = "Video with External Subtitles";
        private const string ProgressFile = "current_segment.json";

        private List<SubtitleSegment> _subtitleData = new List<SubtitleSegment>();
        private int _currentSubIdx;
        private int _currentRepetition;
        private string _state = "playing_normal";
        private int _segmentStartMs;
        private int _segmentEndMs;
        private bool _inSubtitleSegment;
        private string _videoPath;
        private MediaPlayer _mediaPlayer;
        private DispatcherTimer _timer;
        private DispatcherTimer _positionTimer;
        private TimeSpan _currentPosition;
        private bool _isPlaying = false;

        public MainWindow()
        {
            InitializeComponent();
            Title = WindowTitle;
            Width = 800;
            Height = 600;

            // Initialize MediaPlayer
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            // Set up the media element inside the Canvas
            var hostVisual = new VisualHost { Visual = new MediaVisual(_mediaPlayer) };
            videoElement.Children.Add(hostVisual);

            // Make the visual host fill the canvas
            Canvas.SetLeft(hostVisual, 0);
            Canvas.SetTop(hostVisual, 0);

            // Set up timer for subtitle and segment handling
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += UpdateSubtitlesAndLogic;
            _timer.Start();

            // Set up timer for position tracking (since MediaPlayer doesn't have Position events)
            _positionTimer = new DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(50);
            _positionTimer.Tick += UpdatePosition;

            // Event handlers for keys
            KeyDown += MainWindow_KeyDown;

            // Load video and subtitles on startup
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 2)
            {
                LoadVideoAndSubtitles(args[1], args[2]);
            }
            else
            {
                // Show file dialogs to select files
                var videoFile = ShowOpenFileDialog("Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All Files|*.*");
                if (string.IsNullOrEmpty(videoFile)) return;

                var subtitleFile = Path.ChangeExtension(videoFile, null) + "_word_merge_translated.srt";
                if (!File.Exists(subtitleFile))
                {
                    subtitleFile = ShowOpenFileDialog("Subtitle Files|*.srt|All Files|*.*");
                    if (string.IsNullOrEmpty(subtitleFile)) return;
                }

                LoadVideoAndSubtitles(videoFile, subtitleFile);
            }
        }

        private string ShowOpenFileDialog(string filter)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter
            };

            if (openFileDialog.ShowDialog() == true)
            {
                return openFileDialog.FileName;
            }

            return null;
        }

        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            _positionTimer.Start();
            _isPlaying = true;

            // Ensure the video visual has the right dimensions based on the media
            foreach (var child in videoElement.Children)
            {
                if (child is VisualHost host)
                {
                    host.Width = _mediaPlayer.NaturalVideoWidth;
                    host.Height = _mediaPlayer.NaturalVideoHeight;
                }
            }

            // Set initial position if loading from saved state
            if (_subtitleData.Count > 0)
            {
                _mediaPlayer.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
            }
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            _isPlaying = false;
            _positionTimer.Stop();
        }

        private void MediaPlayer_MediaFailed(object sender, ExceptionEventArgs e)
        {
            MessageBox.Show($"Failed to load media: {e.ErrorException.Message}");
            _isPlaying = false;
            _positionTimer.Stop();
        }

        private void UpdatePosition(object sender, EventArgs e)
        {
            _currentPosition = _mediaPlayer.Position;
        }

        private void LoadVideoAndSubtitles(string videoPath, string subtitlePath)
        {
            _videoPath = videoPath;

            // Parse subtitles
            _subtitleData = ParseSubtitleFile(subtitlePath);

            // Load saved position or start from configured position
            int? savedIdx = LoadCurrentSegment(videoPath);
            _currentSubIdx = savedIdx ?? (CfgStartSegment - 1);

            // Keep index in valid range
            _currentSubIdx = Math.Max(0, Math.Min(_currentSubIdx, _subtitleData.Count - 1));

            // Initialize segment times
            if (_subtitleData.Count > 0)
            {
                var segment = _subtitleData[_currentSubIdx];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
            }

            // Set up media player
            _mediaPlayer.Open(new Uri(videoPath));

            // Start playback
            _mediaPlayer.Play();
            _isPlaying = true;

            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";
        }

        private List<SubtitleSegment> ParseSubtitleFile(string path)
        {
            var segments = new List<SubtitleSegment>();
            var lines = File.ReadAllLines(path);

            int index = 0;
            while (index < lines.Length)
            {
                // Skip empty lines
                while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                }

                if (index >= lines.Length) break;

                // Skip subtitle number
                index++;

                if (index >= lines.Length) break;

                // Parse timestamp line
                string timestampLine = lines[index++];
                var timestamps = ParseTimestampLine(timestampLine);
                if (!timestamps.HasValue) continue;

                var (startTime, endTime) = timestamps.Value;

                // Read subtitle text (may span multiple lines)
                string text = "";
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    text += lines[index] + "\n";
                    index++;
                }

                // Remove trailing newline
                if (text.EndsWith("\n"))
                    text = text.Substring(0, text.Length - 1);

                segments.Add(new SubtitleSegment
                {
                    StartMs = (int)startTime.TotalMilliseconds,
                    EndMs = (int)endTime.TotalMilliseconds,
                    Text = text
                });
            }

            return segments;
        }

        private (TimeSpan, TimeSpan)? ParseTimestampLine(string line)
        {
            // Example: "00:01:22,490 --> 00:01:24,725"
            string[] parts = line.Split(new[] { " --> " }, StringSplitOptions.None);
            if (parts.Length != 2) return null;

            // Parse timestamps
            bool startOk = TryParseTimestamp(parts[0], out TimeSpan startTime);
            bool endOk = TryParseTimestamp(parts[1], out TimeSpan endTime);

            if (!startOk || !endOk) return null;

            return (startTime, endTime);
        }

        private bool TryParseTimestamp(string timestamp, out TimeSpan result)
        {
            // Handle timestamps with comma as decimal separator (00:01:22,490)
            timestamp = timestamp.Replace(',', '.');
            return TimeSpan.TryParse(timestamp, out result);
        }

        private int? LoadCurrentSegment(string videoPath)
        {
            if (!File.Exists(ProgressFile)) return null;

            try
            {
                string json = File.ReadAllText(ProgressFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);

                if (data.ContainsKey(videoPath) && data[videoPath].CurrentSegment.HasValue)
                {
                    Console.WriteLine($"Current segment exists: {data[videoPath].CurrentSegment}");
                    return data[videoPath].CurrentSegment;
                }
                else
                {
                    Console.WriteLine("Key does not exist");
                    return 1;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not load current_segment from file: {e.Message}");
                return 1; // Default to 1 instead of throwing
            }
        }

        private void SaveCurrentSegment(string videoPath, int currentSegment)
        {
            Dictionary<string, ProgressData> data = new Dictionary<string, ProgressData>();

            // Try to read existing data to prevent overwriting
            if (File.Exists(ProgressFile))
            {
                try
                {
                    string json = File.ReadAllText(ProgressFile);
                    data = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);
                }
                catch (JsonException)
                {
                    Console.WriteLine("Warning: JSON file is empty or corrupted. Creating a new one.");
                }
            }

            // Update the dictionary with the new segment
            data[videoPath] = new ProgressData { CurrentSegment = currentSegment };

            // Save updated data back to the file
            try
            {
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProgressFile, json);
                Console.WriteLine($"Saved current_segment = {currentSegment} for {videoPath} in {ProgressFile}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not save current_segment to file: {e.Message}");
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Only process keys when this window is active
            if (!IsActive) return;

            if (e.Key == Key.Left)
            {
                _currentSubIdx = Math.Max(0, _currentSubIdx - 1);
                var segment = _subtitleData[_currentSubIdx];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                _currentRepetition = 0;
                _state = "playing_segment";
                _inSubtitleSegment = true;
                _mediaPlayer.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                _mediaPlayer.Play();
                _isPlaying = true;
                Console.WriteLine($"Left arrow key pressed! {_currentSubIdx}, {_subtitleData.Count}, {_state}, {_segmentStartMs}, {_segmentEndMs}");
            }
            else if (e.Key == Key.Right)
            {
                _currentSubIdx = Math.Min(_subtitleData.Count - 1, _currentSubIdx + 1);
                var segment = _subtitleData[_currentSubIdx];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                _currentRepetition = 0;
                _state = "playing_segment";
                _inSubtitleSegment = true;
                _mediaPlayer.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                _mediaPlayer.Play();
                _isPlaying = true;
                Console.WriteLine($"Right arrow key pressed! {_currentSubIdx}, {_subtitleData.Count}, {_state}, {_segmentStartMs}, {_segmentEndMs}");
            }
            else if (e.Key == Key.Space)
            {
                if (_isPlaying)
                {
                    _mediaPlayer.Pause();
                    _isPlaying = false;
                    _state = "user_paused";
                }
                else
                {
                    _mediaPlayer.Play();
                    _isPlaying = true;
                    if (_inSubtitleSegment)
                        _state = "playing_segment";
                    else
                        _state = "playing_normal";
                }
                Console.WriteLine($"Space key pressed! {_currentSubIdx}, {_subtitleData.Count}, {_state}");
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void IncreaseDelay_Click(object sender, RoutedEventArgs e)
        {
            _subtitleExtraDurationMs += 1000;
            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";
            Console.WriteLine($"Increased delay to {_subtitleExtraDurationMs} ms");
        }

        private void DecreaseDelay_Click(object sender, RoutedEventArgs e)
        {
            _subtitleExtraDurationMs -= 1000;
            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";
            Console.WriteLine($"Decreased delay to {_subtitleExtraDurationMs} ms");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveCurrentSegment(_videoPath, _currentSubIdx);

            // Clean up resources
            _timer.Stop();
            _positionTimer.Stop();
            _mediaPlayer.Close();
        }

        private int? FindSubtitleAtTime(int currentMs)
        {
            for (int i = 0; i < _subtitleData.Count; i++)
            {
                var segment = _subtitleData[i];
                if (segment.StartMs <= currentMs && currentMs <= segment.EndMs)
                {
                    return i;
                }
            }
            return null;
        }

        private void UpdateSubtitlesAndLogic(object sender, EventArgs e)
        {
            if (!_isPlaying && _state != "user_paused" && _state != "waiting_pause")
            {
                return;
            }

            int currentMs = (int)_currentPosition.TotalMilliseconds;

            // Handle continuous playback mode
            if (_state == "playing_normal")
            {
                // Check if we've entered a subtitle segment
                int? subtitleIdx = FindSubtitleAtTime(currentMs);
                if (subtitleIdx.HasValue && subtitleIdx.Value != _currentSubIdx)
                {
                    _currentSubIdx = subtitleIdx.Value;
                    var segment = _subtitleData[_currentSubIdx];
                    _segmentStartMs = segment.StartMs;
                    _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                    _currentRepetition = 0;
                    _inSubtitleSegment = true;
                    _state = "playing_segment";
                    Console.WriteLine($"Entered subtitle segment {_currentSubIdx + 1}");
                }

                // Update subtitle display
                UpdateSubtitles();
                return;
            }

            // Handle repetition logic when in a subtitle segment
            if (_state == "playing_segment")
            {
                // Update subtitle display
                UpdateSubtitles();

                // Check if we've reached the end of the segment
                if (currentMs >= _segmentEndMs)
                {
                    _currentRepetition++;
                    _mediaPlayer.Pause();
                    _isPlaying = false;

                    if (_currentRepetition < CfgRepetitions)
                    {
                        // Need to repeat the same segment again
                        _state = "waiting_pause";
                        Console.WriteLine($"Pausing before repeating segment {_currentSubIdx + 1} / {_subtitleData.Count}");

                        var timer = new DispatcherTimer();
                        timer.Interval = TimeSpan.FromMilliseconds(CfgDelayMs);
                        timer.Tick += (s, args) =>
                        {
                            AfterRepetitionPause();
                            timer.Stop();
                        };
                        timer.Start();
                    }
                    else
                    {
                        // Completed the required repetitions of this segment
                        _state = "waiting_pause";
                        Console.WriteLine($"Pausing after completing segment {_currentSubIdx + 1} / {_subtitleData.Count}");

                        var timer = new DispatcherTimer();
                        timer.Interval = TimeSpan.FromMilliseconds(0);
                        timer.Tick += (s, args) =>
                        {
                            AfterSegmentPause();
                            timer.Stop();
                        };
                        timer.Start();
                    }
                }
            }

            // Update subtitles in other states as needed
            if (_state == "user_paused")
            {
                UpdateSubtitles();
            }
        }

        private void AfterRepetitionPause()
        {
            if (_state == "waiting_pause")
            {
                // Seek back to start of segment for repetition
                _mediaPlayer.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                _mediaPlayer.Play();
                _isPlaying = true;
                _state = "playing_segment";
                Console.WriteLine($"Repeating segment {_currentSubIdx + 1} / {_subtitleData.Count} (repetition {_currentRepetition + 1})");
            }
        }

        private void AfterSegmentPause()
        {
            if (_state == "waiting_pause")
            {
                // Continue playing normally - don't seek!
                _mediaPlayer.Play();
                _isPlaying = true;
                _state = "playing_normal";
                _inSubtitleSegment = false;
                Console.WriteLine($"Continuing normal playback after segment {_currentSubIdx + 1}");

                // Check if we're at the last subtitle, and if so, increment the index for proper saving
                if (_currentSubIdx == _subtitleData.Count - 1)
                {
                    _currentSubIdx++;
                }
            }
        }

        private void UpdateSubtitles()
        {
            if (_state == "waiting_pause")
            {
                return;
            }

            int currentMs = (int)_currentPosition.TotalMilliseconds;
            string text = "";

            // In normal playback mode, try to find the current subtitle by time
            if (_state == "playing_normal")
            {
                int? subtitleIdx = FindSubtitleAtTime(currentMs);
                if (subtitleIdx.HasValue)
                {
                    text = _subtitleData[subtitleIdx.Value].Text;
                }
            }
            // In segment or paused modes, use the current_sub_idx
            else
            {
                if (_currentSubIdx < _subtitleData.Count)
                {
                    text = _subtitleData[_currentSubIdx].Text;
                }
            }

            // Only update if there's a difference
            if (subtitleTextBlock.Text != text)
            {
                subtitleTextBlock.Text = text;
            }
        }
    }

    // Helper classes for MediaPlayer integration
    public class MediaVisual : DrawingVisual
    {
        public MediaVisual(MediaPlayer player)
        {
            using (DrawingContext dc = RenderOpen())
            {
                dc.DrawVideo(player, new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight));
            }
        }
    }

    public class VisualHost : Viewbox
    {
        private Visual _visual;

        public Visual Visual
        {
            get { return _visual; }
            set
            {
                _visual = value;

                // Create a presenter for the visual
                var presenter = new Grid();
                var brush = new VisualBrush(_visual);
                presenter.Background = brush;

                // Set as child of this Viewbox
                this.Child = presenter;
                this.Stretch = Stretch.Uniform;
                this.HorizontalAlignment = HorizontalAlignment.Stretch;
                this.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
    }

    public class SubtitleSegment
    {
        public int StartMs { get; set; }
        public int EndMs { get; set; }
        public string Text { get; set; }
    }

    public class ProgressData
    {
        public int? CurrentSegment { get; set; }
    }
}