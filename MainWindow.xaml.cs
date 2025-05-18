using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Globalization;

namespace SubtitleVideoPlayerWpf
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
        private string _state = "playing_normal"; // playing_normal, playing_segment, user_paused, waiting_pause
        private int _segmentStartMs;
        private int _segmentEndMs;
        private bool _inSubtitleSegment;
        private string _videoPath;
        private DispatcherTimer _timer;
        private bool _isPlaying = false;

        public MainWindow()
        {
            InitializeComponent();
            Title = WindowTitle;
            Width = 800;
            Height = 600;

            // Set up timer for subtitle and segment handling
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100); // Check every 100ms
            _timer.Tick += Timer_Tick;
            _timer.Start();

            this.KeyDown += MainWindow_KeyDown;
            this.Closing += Window_Closing;

            // Attempt to load last video and progress
            LoadLastState();

            // Prompt for video and subtitle if not loaded
            if (string.IsNullOrEmpty(_videoPath) || _subtitleData.Count == 0)
            {
                PromptForFiles();
            }
        }

        private void PromptForFiles()
        {
            MessageBoxResult result = MessageBox.Show("Do you want to load a video and subtitle file?", "Load Files", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Title = "Select Video File",
                    Filter = "Video Files|*.mp4;*.avi;*.mkv;*.wmv|All Files|*.*"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    string videoFilePath = openFileDialog.FileName;

                    openFileDialog.Title = "Select Subtitle File (SRT)";
                    openFileDialog.Filter = "SRT Files|*.srt|All Files|*.*";

                    if (openFileDialog.ShowDialog() == true)
                    {
                        string subtitleFilePath = openFileDialog.FileName;
                        LoadVideoAndSubtitles(videoFilePath, subtitleFilePath);
                    }
                }
            }
        }

        private void LoadVideoAndSubtitles(string videoPath, string subtitlePath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            {
                MessageBox.Show($"Video file not found: {videoPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
            {
                MessageBox.Show($"Subtitle file not found: {subtitlePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _videoPath = videoPath;
            videoElement.Source = new Uri(_videoPath);

            try
            {
                _subtitleData = LoadSrt(subtitlePath);
                if (_subtitleData.Count == 0)
                {
                    MessageBox.Show("No subtitles found or failed to parse SRT file.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading SRT file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _subtitleData.Clear();
            }

            int lastSegment = LoadLastSegmentForVideo(_videoPath);
            _currentSubIdx = Math.Max(0, Math.Min(lastSegment, _subtitleData.Count - 1));
            _currentRepetition = 0;
            _state = "playing_normal";
            _inSubtitleSegment = false;

            if (_subtitleData.Count > 0 && _currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
            }

            videoElement.Play();
            _isPlaying = true;
            _timer.Start();

            Title = $"{WindowTitle} - {Path.GetFileName(videoPath)}";
            UpdateSubtitleDisplay();
        }

        private List<SubtitleSegment> LoadSrt(string filePath)
        {
            var subtitles = new List<SubtitleSegment>();
            var lines = File.ReadAllLines(filePath);
            int i = 0;
            while (i < lines.Length)
            {
                if (int.TryParse(lines[i], out _)) // Check for segment number
                {
                    i++;
                    if (i < lines.Length && lines[i].Contains("-->"))
                    {
                        string timeLine = lines[i];
                        var parts = timeLine.Split(new[] { " --> " }, StringSplitOptions.None);
                        if (parts.Length == 2)
                        {
                            TimeSpan startTime = ParseSrtTime(parts[0]);
                            TimeSpan endTime = ParseSrtTime(parts[1]);
                            i++;
                            string text = "";
                            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                            {
                                text += lines[i] + Environment.NewLine;
                                i++;
                            }
                            subtitles.Add(new SubtitleSegment
                            {
                                StartMs = (int)startTime.TotalMilliseconds,
                                EndMs = (int)endTime.TotalMilliseconds,
                                Text = text.Trim()
                            });
                        }
                    }
                }
                i++;
            }
            return subtitles;
        }

        private TimeSpan ParseSrtTime(string srtTime)
        {
            // Format: 00:00:20,000
            if (TimeSpan.TryParseExact(srtTime.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan result))
            {
                return result;
            }
            return TimeSpan.Zero;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isPlaying || _subtitleData.Count == 0 || _state == "user_paused" || _state == "waiting_pause")
                return;

            int currentMs = (int)videoElement.Position.TotalMilliseconds;
            UpdateSubtitlesAndLogic(currentMs);
        }

        private void UpdateSubtitlesAndLogic(int currentMs)
        {
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
                    SaveCurrentSegment(_videoPath, _currentSubIdx);
                }

                // Update subtitle display
                UpdateSubtitleDisplay();
                return;
            }

            // Handle repetition logic when in a subtitle segment
            if (_state == "playing_segment")
            {
                // Update subtitle display
                UpdateSubtitleDisplay();

                // Check if we've reached the end of the segment
                if (currentMs >= _segmentEndMs)
                {
                    _currentRepetition++;
                    videoElement.Pause();
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
                            AfterRepetitionPause(timer);
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
                            AfterSegmentPause(timer);
                        };
                        timer.Start();
                    }
                }
            }
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

        private void AfterRepetitionPause(DispatcherTimer timer)
        {
            timer.Stop();

            if (_state == "waiting_pause")
            {
                // Seek back to start of segment for repetition
                videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                videoElement.Play();
                _isPlaying = true;
                _state = "playing_segment";
                Console.WriteLine($"Repeating segment {_currentSubIdx + 1} / {_subtitleData.Count} (repetition {_currentRepetition + 1})");
            }
        }

        private void AfterSegmentPause(DispatcherTimer timer)
        {
            timer.Stop();

            if (_state == "waiting_pause")
            {
                // Continue playing normally - don't seek!
                videoElement.Play();
                _isPlaying = true;
                _state = "playing_normal";
                _inSubtitleSegment = false;
                Console.WriteLine($"Continuing normal playback after segment {_currentSubIdx + 1}");

                // Check if we're at the last subtitle, and if so, increment the index for proper saving
                if (_currentSubIdx == _subtitleData.Count - 1)
                {
                    _currentSubIdx++;
                    SaveCurrentSegment(_videoPath, _currentSubIdx);
                }
            }
        }

        private void UpdateSubtitleDisplay()
        {
            if (_state == "waiting_pause")
            {
                return;
            }

            string text = "";
            string statusInfo = "";

            // In normal playback mode, try to find the current subtitle by time
            if (_state == "playing_normal")
            {
                int? subtitleIdx = FindSubtitleAtTime((int)videoElement.Position.TotalMilliseconds);
                if (subtitleIdx.HasValue)
                {
                    text = _subtitleData[subtitleIdx.Value].Text;
                    statusInfo = $"({subtitleIdx.Value + 1}/{_subtitleData.Count})";
                }
            }
            // In segment or paused modes, use the current_sub_idx
            else
            {
                if (_currentSubIdx < _subtitleData.Count)
                {
                    text = _subtitleData[_currentSubIdx].Text;
                    statusInfo = $"({_currentSubIdx + 1}/{_subtitleData.Count}) [Rep: {_currentRepetition + 1}/{CfgRepetitions}]";
                }
            }

            // Only update if there's a difference
            if (!string.IsNullOrEmpty(text))
                subtitleText.Text = text + (string.IsNullOrEmpty(statusInfo) ? "" : $" {statusInfo}");
            else if (_currentSubIdx >= _subtitleData.Count)
                subtitleText.Text = "End of subtitles.";
            else
                subtitleText.Text = "";
        }

        private void JumpToSegment(int segmentIndex)
        {
            if (segmentIndex >= 0 && segmentIndex < _subtitleData.Count)
            {
                _currentSubIdx = segmentIndex;
                var segment = _subtitleData[segmentIndex];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                _currentRepetition = 0;
                _state = "playing_segment";
                _inSubtitleSegment = true;

                videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                videoElement.Play();
                _isPlaying = true;

                SaveCurrentSegment(_videoPath, _currentSubIdx);
                UpdateSubtitleDisplay();
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
                    data = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json) ??
                           new Dictionary<string, ProgressData>();
                }
                catch (JsonException)
                {
                    Console.WriteLine("Warning: JSON file is empty or corrupted. Creating a new one.");
                }
            }

            // Update the dictionary with the new segment
            data[Path.GetFileName(videoPath)] = new ProgressData { CurrentSegment = currentSegment };

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

        private int LoadLastSegmentForVideo(string videoPath)
        {
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(ProgressFile))
                return CfgStartSegment - 1;

            try
            {
                string json = File.ReadAllText(ProgressFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);

                if (data != null && data.TryGetValue(Path.GetFileName(videoPath), out var progressData) &&
                    progressData.CurrentSegment.HasValue)
                {
                    return progressData.CurrentSegment.Value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load progress: {ex.Message}");
            }

            return CfgStartSegment - 1;
        }

        private void LoadLastState()
        {
            if (!File.Exists(ProgressFile)) return;

            try
            {
                var json = File.ReadAllText(ProgressFile);
                var progressData = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);

                if (progressData != null && progressData.Count > 0)
                {
                    var lastEntry = progressData.First();
                    string videoFileName = lastEntry.Key;
                    int segmentToLoad = lastEntry.Value.CurrentSegment ?? (CfgStartSegment - 1);

                    MessageBoxResult result = MessageBox.Show($"Load last session for '{videoFileName}'?",
                                                "Load Previous Session", MessageBoxButton.YesNo);
                    if (result == MessageBoxResult.Yes)
                    {
                        OpenFileDialog openFileDialog = new OpenFileDialog
                        {
                            Title = $"Select Video File: {videoFileName}",
                            Filter = "Video Files|*.mp4;*.avi;*.mkv;*.wmv|All Files|*.*",
                            FileName = videoFileName
                        };
                        if (openFileDialog.ShowDialog() == true)
                        {
                            string videoFilePath = openFileDialog.FileName;
                            string assumedSubtitlePath = Path.ChangeExtension(videoFilePath, ".srt");

                            if (File.Exists(assumedSubtitlePath))
                            {
                                LoadVideoAndSubtitles(videoFilePath, assumedSubtitlePath);
                                if (_subtitleData.Count > 0 && segmentToLoad < _subtitleData.Count)
                                {
                                    JumpToSegment(segmentToLoad);
                                }
                            }
                            else
                            {
                                MessageBox.Show($"Subtitle for '{videoFileName}' not found at '{assumedSubtitlePath}'. " +
                                                "Please select it manually.");
                                PromptForFiles();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load last state from progress file: {ex.Message}");
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Only process keys when this window is active
            if (!IsActive) return;

            if (e.Key == Key.Left)
            {
                // Previous segment
                _currentSubIdx = Math.Max(0, _currentSubIdx - 1);
                JumpToSegment(_currentSubIdx);
            }
            else if (e.Key == Key.Right)
            {
                // Next segment
                _currentSubIdx = Math.Min(_subtitleData.Count - 1, _currentSubIdx + 1);
                JumpToSegment(_currentSubIdx);
            }
            else if (e.Key == Key.Space)
            {
                if (_isPlaying)
                {
                    videoElement.Pause();
                    _isPlaying = false;
                    _state = "user_paused";
                }
                else
                {
                    videoElement.Play();
                    _isPlaying = true;
                    if (_inSubtitleSegment)
                        _state = "playing_segment";
                    else
                        _state = "playing_normal";
                }
            }
            else if (e.Key == Key.O)
            {
                // Open new files
                videoElement.Stop();
                _isPlaying = false;
                _timer.Stop();
                subtitleText.Text = "";
                _subtitleData.Clear();
                _videoPath = null;
                PromptForFiles();
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        // MediaElement event handlers
        private void MediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isPlaying = true;

            // Set initial position if loading from saved state
            if (_subtitleData.Count > 0 && _currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                videoElement.Position = TimeSpan.FromMilliseconds(segment.StartMs);
            }
        }

        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            subtitleText.Text = "Video ended. Press Space to replay or R to restart.";
        }

        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _isPlaying = false;
            MessageBox.Show($"Failed to load media: {e.ErrorException}", "Media Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveCurrentSegment(_videoPath, _currentSubIdx);

            // Clean up resources
            _timer.Stop();
            videoElement.Source = null;
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