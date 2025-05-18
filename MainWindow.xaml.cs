using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32; // For OpenFileDialog
using System.Globalization; // For parsing subtitle times

using System;
using System.Runtime.InteropServices;

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
        private string _state = "playing_normal"; // playing_normal, in_segment_pause, in_repetition_pause
        private int _segmentStartMs;
        private int _segmentEndMs;
        private bool _inSubtitleSegment;
        private string _videoPath;
        // private MediaPlayer _mediaPlayer; // Keep for potential future use or remove if videoElement handles all.
        // If used, ensure it's not conflicting with videoElement for video rendering.
        private DispatcherTimer _timer; // Timer for subtitle logic
        private DispatcherTimer _positionTimer; // Timer for updating current media position
        private TimeSpan _currentPosition;
        private bool _isPlaying = false;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        public MainWindow()
        {
            InitializeComponent();

            AllocConsole(); // creates a console window
            Console.WriteLine("Console output from WPF!");

            Title = WindowTitle;
            Width = 800; // Initial width
            Height = 600; // Initial height

            // Initialize MediaPlayer (Potentially for non-visual tasks if needed, or remove)
            // _mediaPlayer = new MediaPlayer();
            // _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            // _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            // _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            // The VisualHost and MediaVisual setup is commented out as videoElement is now primary.
            // var hostVisual = new VisualHost { Visual = new MediaVisual(_mediaPlayer) };
            // videoCanvas.Children.Add(hostVisual); // Assuming videoCanvas was the intended parent
            // Canvas.SetLeft(hostVisual, 0);
            // Canvas.SetTop(hostVisual, 0);

            // Ensure videoElement is available
            if (videoElement == null)
            {
                MessageBox.Show("MediaElement (videoElement) not found in XAML.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // Timer for subtitle processing logic
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100); // Check every 100ms
            _timer.Tick += Timer_Tick;

            // Timer for updating current media position
            _positionTimer = new DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(100); // Update position display frequently
            _positionTimer.Tick += UpdatePosition;

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
            // Initialize and display the current delay value
            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";

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
            videoElement.Source = new Uri(_videoPath); // Set source for MediaElement

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
                _subtitleData.Clear(); // Ensure no partial data
            }

            int lastSegment = LoadLastSegmentForVideo(_videoPath);
            _currentSubIdx = Math.Max(0, Math.Min(lastSegment, _subtitleData.Count - 1));
            _currentRepetition = 0;
            _state = "playing_normal";

            if (_subtitleData.Count > 0 && _currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                videoElement.Position = TimeSpan.FromMilliseconds(segment.StartMs);
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
            }

            videoElement.Play(); // Play using MediaElement
            _isPlaying = true;
            _timer.Start();
            _positionTimer.Start(); // Start position timer when media is ready (or here)

            Title = $"{WindowTitle} - {Path.GetFileName(videoPath)}";
            UpdateSubtitleDisplay(); // Initial display
//            JumpToSegment(_currentSubIdx);
        }

        // Add these methods to MainWindow.xaml.cs

        /// <summary>
        /// Increases the subtitle delay by 1000ms
        /// </summary>
        private void IncreaseDelay_Click(object sender, RoutedEventArgs e)
        {
            _subtitleExtraDurationMs += 1000;
            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";

            // Update current segment end time with new delay
            if (_currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
            }

            System.Diagnostics.Debug.WriteLine($"Increased delay to {_subtitleExtraDurationMs} ms");
        }

        /// <summary>
        /// Decreases the subtitle delay by 1000ms (minimum 0)
        /// </summary>
        private void DecreaseDelay_Click(object sender, RoutedEventArgs e)
        {
            _subtitleExtraDurationMs = Math.Max(0, _subtitleExtraDurationMs - 1000);
            delayLabel.Content = $"Delay: {_subtitleExtraDurationMs} ms";

            // Update current segment end time with new delay
            if (_currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
            }

            System.Diagnostics.Debug.WriteLine($"Decreased delay to {_subtitleExtraDurationMs} ms");
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
            // Fallback for slightly different formats if necessary, though srt is usually consistent
            return TimeSpan.Zero;
        }


        private void UpdatePosition(object sender, EventArgs e)
        {
            if (videoElement.Source != null && videoElement.Clock != null) // Check if media is loaded
            {
                _currentPosition = videoElement.Position;
            }
            else if (videoElement.Source != null && videoElement.NaturalDuration.HasTimeSpan) // For cases where Clock might be null but position is valid
            {
                _currentPosition = videoElement.Position;
            }
            else
            {
                _currentPosition = TimeSpan.Zero;
            }

            // Update a status bar or label if you have one for current time
            // e.g., currentTimeLabel.Text = _currentPosition.ToString(@"hh\:mm\:ss");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isPlaying || _subtitleData.Count == 0) return;

            _currentPosition = videoElement.Position; // Get current position from MediaElement
            long currentMs = (long)_currentPosition.TotalMilliseconds;

            UpdateSubtitlesAndLogic(currentMs);
        }

        private void UpdateSubtitlesAndLogic(long currentMs)
        {
            bool segmentChanged = false;

            if (_state == "playing_normal")
            {
                if (!_inSubtitleSegment)
                {
                    if (_currentSubIdx < _subtitleData.Count)
                    {
                        var segment = _subtitleData[_currentSubIdx];
                        if (currentMs >= segment.StartMs)
                        {
                            _inSubtitleSegment = true;
                            _segmentStartMs = segment.StartMs;
                            _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                            _currentRepetition = 0; // Reset repetitions for the new segment
                            UpdateSubtitleDisplay();
                            segmentChanged = true;
                        }
                    }
                }

                if (_inSubtitleSegment)
                {
                    if (currentMs >= _segmentEndMs)
                    {
                        // Segment ended, pause for repetition or move to next
                        videoElement.Pause(); // Pause using MediaElement
                        _isPlaying = false;

                        if (_currentRepetition < CfgRepetitions - 1)
                        {
                            _state = "in_repetition_pause";
                            _currentRepetition++;
                            DispatcherTimer pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CfgDelayMs) };
                            pauseTimer.Tick += (s, args) => { AfterRepetitionPause(pauseTimer); };
                            pauseTimer.Start();
                        }
                        else
                        {
                            _state = "in_segment_pause"; // Pause before moving to the next segment
                            _inSubtitleSegment = false; // Exited current segment logic
                            _currentRepetition = 0; // Reset for next segment
                            _currentSubIdx++;
                            segmentChanged = true;

                            if (_currentSubIdx >= _subtitleData.Count) // End of subtitles
                            {
                                subtitleText.Text = "End of subtitles.";
                                _timer.Stop(); // Stop main logic timer
                                return;
                            }

                            DispatcherTimer pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CfgDelayMs) };
                            pauseTimer.Tick += (s, args) => { AfterSegmentPause(pauseTimer); };
                            pauseTimer.Start();
                        }
                    }
                }
                else if (_currentSubIdx >= _subtitleData.Count) // If not in segment and all subtitles are done
                {
                    subtitleText.Text = "End of subtitles. Press Space to replay last or R to restart.";
                }
            }
            if (segmentChanged) SaveCurrentSegment(_videoPath, _currentSubIdx);
        }


        private void AfterRepetitionPause(DispatcherTimer pauseTimer)
        {
            pauseTimer.Stop();
            videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs); // Seek using MediaElement
            videoElement.Play(); // Play using MediaElement
            _isPlaying = true;
            _state = "playing_normal";
            UpdateSubtitleDisplay(); // Refresh display for repetition
        }

        private void AfterSegmentPause(DispatcherTimer pauseTimer)
        {
            pauseTimer.Stop();
            if (_currentSubIdx < _subtitleData.Count)
            {
                // Prepare for next segment
                var nextSegment = _subtitleData[_currentSubIdx];
                _segmentStartMs = nextSegment.StartMs;
                _segmentEndMs = nextSegment.EndMs + _subtitleExtraDurationMs;
                videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                UpdateSubtitleDisplay(); // Show next subtitle text immediately
            }
            videoElement.Play(); // Play using MediaElement
            _isPlaying = true;
            _state = "playing_normal";
        }


        private void UpdateSubtitleDisplay()
        {
            if (_currentSubIdx >= 0 && _currentSubIdx < _subtitleData.Count)
            {
                var segment = _subtitleData[_currentSubIdx];
                string repetitionInfo = CfgRepetitions > 1 ? $" (Rep: {_currentRepetition + 1}/{CfgRepetitions})" : "";
                subtitleText.Text = $"{_currentSubIdx + 1}/{_subtitleData.Count}{repetitionInfo}: {segment.Text}";
            }
            else if (_currentSubIdx >= _subtitleData.Count && _subtitleData.Count > 0)
            {
                subtitleText.Text = "End of subtitles.";
            }
            else
            {
                subtitleText.Text = "No subtitle loaded or at the beginning.";
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (_subtitleData.Count == 0 && e.Key != Key.O) // Allow opening files even if no subs
            {
                subtitleText.Text = "No subtitles loaded. Press 'O' to open files.";
                return;
            }

            long currentMs = (long)videoElement.Position.TotalMilliseconds;

            switch (e.Key)
            {
                case Key.Space:
                    if (_isPlaying)
                    {
                        videoElement.Pause();
                        _isPlaying = false;
                        _timer.Stop(); // Stop subtitle logic timer when paused
                        _positionTimer.Stop();
                    }
                    else
                    {
                        videoElement.Play();
                        _isPlaying = true;
                        _timer.Start(); // Resume subtitle logic timer
                        _positionTimer.Start();
                        // If paused during a delay, ensure state is reset to playing_normal
                        if (_state != "playing_normal") _state = "playing_normal";

                    }
                    break;

                case Key.Right: // Next subtitle
                    if (_currentSubIdx < _subtitleData.Count - 1)
                    {
                        _currentSubIdx++;
                        JumpToSegment(_currentSubIdx);
                    }
                    break;

                case Key.Left: // Previous subtitle
                    if (_currentSubIdx > 0)
                    {
                        _currentSubIdx--;
                        JumpToSegment(_currentSubIdx);
                    }
                    break;

                case Key.PageDown: // Forward 5 segments or to end
                    _currentSubIdx = Math.Min(_currentSubIdx + 5, _subtitleData.Count > 0 ? _subtitleData.Count - 1 : 0);
                    if (_subtitleData.Count > 0) JumpToSegment(_currentSubIdx);
                    break;

                case Key.PageUp: // Backward 5 segments or to start
                    _currentSubIdx = Math.Max(_currentSubIdx - 5, 0);
                    if (_subtitleData.Count > 0) JumpToSegment(_currentSubIdx);
                    break;

                case Key.Home: // Go to first subtitle
                    if (_subtitleData.Count > 0)
                    {
                        _currentSubIdx = 0;
                        JumpToSegment(_currentSubIdx);
                    }
                    break;

                case Key.End: // Go to last subtitle
                    if (_subtitleData.Count > 0)
                    {
                        _currentSubIdx = _subtitleData.Count - 1;
                        JumpToSegment(_currentSubIdx);
                    }
                    break;

                case Key.R: // Restart video from current segment or from beginning if at end
                    if (_subtitleData.Count > 0)
                    {
                        if (_currentSubIdx >= _subtitleData.Count) _currentSubIdx = 0; // If at end, restart from beginning
                        JumpToSegment(_currentSubIdx);
                    }
                    else
                    {
                        videoElement.Position = TimeSpan.Zero;
                        if (_isPlaying) videoElement.Play();
                    }
                    break;

                case Key.O: // Open new video/subtitle
                    videoElement.Stop(); // Stop current media
                    _isPlaying = false;
                    _timer.Stop();
                    _positionTimer.Stop();
                    subtitleText.Text = "";
                    _subtitleData.Clear();
                    _videoPath = null;
                    PromptForFiles();
                    break;
            }
        }

        private void JumpToSegment(int segmentIndex)
        {
            Console.WriteLine("segmentIndex:" + segmentIndex);  // prints with newline
            if (segmentIndex >= 0 && segmentIndex < _subtitleData.Count)
            {
                Console.WriteLine("segmentIndex2:" + segmentIndex);  // prints with newline
                _currentSubIdx = segmentIndex;
                var segment = _subtitleData[segmentIndex];
                _segmentStartMs = segment.StartMs;
                _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
                videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
                _inSubtitleSegment = false; // Force re-evaluation for entering the segment
                _currentRepetition = 0;
                _state = "playing_normal"; // Reset state
                UpdateSubtitleDisplay();
                if (!_isPlaying) // If paused, play after jump
                {
                    videoElement.Play();
                    _isPlaying = true;
                }
                if (!_timer.IsEnabled) _timer.Start();
                SaveCurrentSegment(_videoPath, _currentSubIdx);
            }
        }

        // Add this method to support the improved last session loading

        /// <summary>
        /// Saves the current session to the progress file including the full path of the video
        /// </summary>
        private void SaveCurrentSegment(string videoPath, int currentSegment)
        {
            if (string.IsNullOrEmpty(videoPath)) return;
            try
            {
                var progressData = new Dictionary<string, ProgressData>();
                if (File.Exists(ProgressFile))
                {
                    var json = File.ReadAllText(ProgressFile);
                    progressData = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json) ??
                                  new Dictionary<string, ProgressData>();
                }

                // Save using the filename as the key, but store the full path for faster loading next time
                string fileName = Path.GetFileName(videoPath);
                progressData[fileName] = new ProgressData
                {
                    CurrentSegment = currentSegment,
                    FullPath = videoPath
                };

                var updatedJson = JsonSerializer.Serialize(progressData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProgressFile, updatedJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save progress: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the ProgressData class to include the full path
        /// </summary>
        public class ProgressData
        {
            public int? CurrentSegment { get; set; }
            public string FullPath { get; set; }
        }

        private int LoadLastSegmentForVideo(string videoName)
        {
            if (string.IsNullOrEmpty(videoName) || !File.Exists(ProgressFile)) return CfgStartSegment - 1;
            try
            {
                var json = File.ReadAllText(ProgressFile);
                var progressData = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);
                if (progressData != null && progressData.TryGetValue(Path.GetFileName(videoName), out var data) && data.CurrentSegment.HasValue)
                {
                    return data.CurrentSegment.Value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load progress: {ex.Message}");
            }
            return CfgStartSegment - 1; // Default to configured start if not found or error
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
                    // For simplicity, use the first video entry found
                    var lastEntry = progressData.First();
                    string videoFileName = lastEntry.Key;
                    int segmentToLoad = lastEntry.Value.CurrentSegment ?? (CfgStartSegment - 1);

                    // Try to find the video file
                    string videoPath = FindVideoFile(videoFileName);
                    if (!string.IsNullOrEmpty(videoPath))
                    {
                        // Look for the subtitle file with specific suffix
                        string subtitlePath = Path.Combine(
                            Path.GetDirectoryName(videoPath),
                            Path.GetFileNameWithoutExtension(videoPath) + "_word_merge_translated.srt");

                        if (File.Exists(subtitlePath))
                        {
                            // Load video and subtitles directly without prompting
                            LoadVideoAndSubtitles(videoPath, subtitlePath);

                            // Jump to last saved segment
                            if (_subtitleData.Count > 0 && segmentToLoad < _subtitleData.Count)
                            {
                                JumpToSegment(segmentToLoad);
                            }
                            return;
                        }
                    }

                    // If automatic loading failed, prompt the user
                    PromptForFiles();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load last state from progress file: {ex.Message}");
                PromptForFiles();
            }
        }

        /// <summary>
        /// Attempts to find the video file using the filename from the progress file.
        /// First tries to find the exact file in the current directory,
        /// then prompts the user if it can't be found automatically.
        /// </summary>
        // Add this code to FindVideoFile method to use the stored full path
        private string FindVideoFile(string videoFileName)
        {
            try
            {
                // First try to use the full path from the progress file
                var json = File.ReadAllText(ProgressFile);
                var progressData = JsonSerializer.Deserialize<Dictionary<string, ProgressData>>(json);

                if (progressData != null && progressData.TryGetValue(videoFileName, out var data) &&
                    !string.IsNullOrEmpty(data.FullPath) && File.Exists(data.FullPath))
                {
                    return data.FullPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get full path from progress file: {ex.Message}");
            }

            // If full path not available, continue with the original implementation...
            // [Original implementation from previous artifact]

            // First, try to find the file in the current directory
            string[] videoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv" };
            string currentDir = Directory.GetCurrentDirectory();

            // Check if the file exists with any of the common video extensions
            foreach (var ext in videoExtensions)
            {
                string possiblePath = Path.Combine(currentDir, videoFileName);
                if (File.Exists(possiblePath))
                {
                    return possiblePath;
                }
            }

            // If not found, look in common video directories
            string[] commonVideoDirs = {
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

            foreach (var dir in commonVideoDirs)
            {
                if (Directory.Exists(dir))
                {
                    foreach (var ext in videoExtensions)
                    {
                        string possiblePath = Path.Combine(dir, videoFileName);
                        if (File.Exists(possiblePath))
                        {
                            return possiblePath;
                        }
                    }
                }
            }

            // If still not found, quietly prompt the user without mentioning "last session"
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Select Video File",
                Filter = "Video Files|*.mp4;*.avi;*.mkv;*.wmv|All Files|*.*",
                FileName = videoFileName
            };

            if (openFileDialog.ShowDialog() == true)
            {
                return openFileDialog.FileName;
            }

            return null;
        }




        // --- Event Handlers for MediaElement ---
        private void MediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isPlaying = true; // Set based on MediaElement's state or after Play() command
            _timer.Start();
            _positionTimer.Start();
            if (videoElement.NaturalDuration.HasTimeSpan)
            {
                // You can set a slider's maximum value here if you have one
                // timeSlider.Maximum = videoElement.NaturalDuration.TimeSpan.TotalSeconds;
            }
            UpdateSubtitleDisplay(); // Ensure subtitle is displayed if media opens at a segment
        }

        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            _timer.Stop();
            _positionTimer.Stop();
            subtitleText.Text = "Video ended. Press Space to replay or R to restart.";
            // Optionally, reset to beginning or a specific point
            videoElement.Stop(); // Resets position to beginning
            _currentSubIdx = _subtitleData.Count > 0 ? _subtitleData.Count - 1 : 0; // Go to a state where 'R' would restart from beginning
            SaveCurrentSegment(_videoPath, _currentSubIdx); // Save end state
        }

        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _isPlaying = false;
            _timer.Stop();
            _positionTimer.Stop();
            MessageBox.Show($"Media failed to load or play: {e.ErrorException.Message}", "Media Error", MessageBoxButton.OK, MessageBoxImage.Error);
            subtitleText.Text = "Error loading video.";
        }

        // --- Event Handlers for MediaPlayer (if _mediaPlayer is still used for something) ---
        // These might be redundant if videoElement handles everything.
        /*
        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            // This would be for _mediaPlayer, not videoElement
            // _isPlaying = true; // If _mediaPlayer is the source of truth
            // _timer.Start();
            // _positionTimer.Start();
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            // _isPlaying = false;
            // _timer.Stop();
            // _positionTimer.Stop();
        }

        private void MediaPlayer_MediaFailed(object sender, ExceptionEventArgs e)
        {
            // _isPlaying = false;
            // MessageBox.Show($"Media player failed: {e.ErrorException.Message}", "Error");
        }
        */

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveCurrentSegment(_videoPath, _currentSubIdx);

            // Clean up resources
            _timer?.Stop();
            _positionTimer?.Stop();

            // Properly dispose of MediaElement resources
            videoElement?.Stop(); // Stop playback
            videoElement?.Close(); // Close the media: releases file handles
            videoElement.Source = null;

            // If _mediaPlayer was used:
            // _mediaPlayer?.Close();
        }
    }

    // Helper classes for MediaPlayer integration (MediaVisual, VisualHost)
    // These are likely NOT NEEDED if MediaElement is used directly for video display.
    // Kept for reference or if MediaPlayer has other specific uses.
    /*
    public class MediaVisual : DrawingVisual
    {
        public MediaVisual(MediaPlayer player)
        {
            if (player != null && player.HasVideo) // Check if player has video before drawing
            {
                using (DrawingContext dc = RenderOpen())
                {
                    // Ensure NaturalVideoWidth/Height are valid
                    if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
                    {
                        dc.DrawVideo(player, new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight));
                    }
                }
            }
        }
    }

    public class VisualHost : Viewbox // Changed from FrameworkElement to Viewbox for auto-scaling
    {
        private Visual _visual;

        public Visual Visual
        {
            get { return _visual; }
            set
            {
                _visual = value;

                // Create a presenter for the visual
                var presenter = new Grid(); // Using Grid as a simple container
                if (_visual != null)
                {
                    var brush = new VisualBrush(_visual);
                    presenter.Background = brush;
                }
                
                // Set as child of this Viewbox
                this.Child = presenter;
                this.Stretch = Stretch.Uniform; // Ensure video scales uniformly
                this.HorizontalAlignment = HorizontalAlignment.Stretch;
                this.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
    }
    */

    public class SubtitleSegment
    {
        public int StartMs { get; set; }
        public int EndMs { get; set; }
        public string Text { get; set; }
    }

    public class ProgressData
    {
        public int? CurrentSegment { get; set; } // Nullable if no segment is saved
    }
}