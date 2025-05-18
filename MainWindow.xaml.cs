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
using System.Linq; // Added for FirstOrDefault

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

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += Timer_Tick;
            // Timer will be started after files are loaded

            this.KeyDown += MainWindow_KeyDown;
            this.Closing += Window_Closing;

            LoadLastState(); // Attempt to load last video and progress

            if (string.IsNullOrEmpty(_videoPath) || _subtitleData.Count == 0)
            {
                PromptForFiles();
            }
        }

        // Add these methods to the MainWindow class

        private void UpdateDurationDisplay()
        {
            // Update the label to show current subtitle duration adjustment
            subtitleDurationAdjustLabel.Content = $"Subtitle Duration: {(_subtitleExtraDurationMs >= 0 ? "+" : "")}{_subtitleExtraDurationMs / 1000}s";

            // If we're in a subtitle segment, update the end time for the current segment
            if (_inSubtitleSegment && _currentSubIdx >= 0 && _currentSubIdx < _subtitleData.Count)
            {
                _segmentEndMs = _subtitleData[_currentSubIdx].EndMs + _subtitleExtraDurationMs;
                Console.WriteLine($"Updated segment {_currentSubIdx + 1} end time: {_segmentEndMs}ms (original: {_subtitleData[_currentSubIdx].EndMs}ms, adjustment: {_subtitleExtraDurationMs}ms)");
            }

            // We also need to update the current visible subtitle if we're in normal playback mode
            if (_state == "playing_normal")
            {
                UpdateSubtitleDisplay();
            }
        }

        private void IncreaseDurationButton_Click(object sender, RoutedEventArgs e)
        {
            // Increase subtitle duration by 1 second (1000 ms)
            _subtitleExtraDurationMs += 1000;
            UpdateDurationDisplay();
        }

        private void DecreaseDurationButton_Click(object sender, RoutedEventArgs e)
        {
            // Decrease subtitle duration by 1 second (1000 ms), but don't go below -original duration
            // This would make sure subtitles don't end before they start
            int minAdjustment = -1000; // Allow at most 1 second reduction

            if (_subtitleExtraDurationMs > minAdjustment)
            {
                _subtitleExtraDurationMs -= 1000;
                UpdateDurationDisplay();
            }
        }

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            videoElement.Stop();
            _isPlaying = false;
            if (_timer != null)
            {
                _timer.Stop();
            }
            subtitleText.Text = "";
            _subtitleData.Clear();
            _videoPath = null;
            PromptForFiles();
        }

        private void PromptForFiles()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Select Video File",
                Filter = "Video Files|*.mp4;*.avi;*.mkv;*.wmv|All Files|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string videoFilePath = openFileDialog.FileName;
                string defaultSubtitleName = Path.GetFileNameWithoutExtension(videoFilePath) + "_word_merge_translated.srt";
                string defaultSubtitlePath = Path.Combine(Path.GetDirectoryName(videoFilePath), defaultSubtitleName);

                if (File.Exists(defaultSubtitlePath))
                {
                    LoadVideoAndSubtitles(videoFilePath, defaultSubtitlePath);
                }
                else
                {
                    openFileDialog.Title = "Select Subtitle File (SRT)";
                    openFileDialog.Filter = "SRT Files|*.srt|All Files|*.*";
                    openFileDialog.InitialDirectory = Path.GetDirectoryName(videoFilePath);
                    openFileDialog.FileName = ""; // Clear previous selection

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
                _videoPath = null; // Ensure it's null so PromptForFiles might be called again if needed
                return;
            }
            if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
            {
                MessageBox.Show($"Subtitle file not found: {subtitlePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _videoPath = null; // Ensure it's null
                return;
            }

            _videoPath = videoPath; // Set _videoPath here
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

            int lastSegment = LoadLastSegmentForVideo(Path.GetFileName(videoPath)); // Use filename as key
            _currentSubIdx = 0; // Default to 0

            if (_subtitleData.Count > 0)
            {
                _currentSubIdx = Math.Max(0, Math.Min(lastSegment, _subtitleData.Count - 1)); // Ensure it's within bounds
                                                                                              // If lastSegment indicated "after last subtitle", this will take the last valid index.
                if (lastSegment >= _subtitleData.Count && _subtitleData.Count > 0)
                {
                    _currentSubIdx = _subtitleData.Count - 1;
                }
                else if (lastSegment < _subtitleData.Count)
                {
                    _currentSubIdx = Math.Max(0, lastSegment);
                }
            }


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
            else if (_subtitleData.Count == 0) // No subtitles, play from start
            {
                videoElement.Position = TimeSpan.Zero;
            }


            videoElement.Play();
            _isPlaying = true;
            if (_timer != null && !_timer.IsEnabled)
            {
                _timer.Start();
            }

            Title = $"{WindowTitle} - {Path.GetFileName(videoPath)}";
            UpdateSubtitleDisplay();
        }

        private List<SubtitleSegment> LoadSrt(string filePath)
        {
            var subtitles = new List<SubtitleSegment>();
            // Robust SRT parsing might be needed for malformed files. This is a basic parser.
            try
            {
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
                            else { i++; } // Malformed time line
                        }
                        else { i++; } // Expected time line not found
                    }
                    else { i++; } // Not a segment number
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading SRT file content: {ex.Message}", "SRT Read Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return new List<SubtitleSegment>(); // Return empty list on error
            }
            return subtitles;
        }


        private TimeSpan ParseSrtTime(string srtTime)
        {
            if (TimeSpan.TryParseExact(srtTime.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan result))
            {
                return result;
            }
            // Try parsing without milliseconds if above fails (e.g., 00:00:20)
            if (TimeSpan.TryParseExact(srtTime, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return TimeSpan.Zero;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isPlaying || videoElement.Source == null || _subtitleData.Count == 0 || _state == "user_paused" || _state == "waiting_pause")
                return;

            int currentMs = (int)videoElement.Position.TotalMilliseconds;
            UpdateSubtitlesAndLogic(currentMs);
        }

        private void UpdateSubtitlesAndLogic(int currentMs)
        {
            if (_state == "playing_normal")
            {
                int? subtitleIdx = FindSubtitleAtTime(currentMs);
                if (subtitleIdx.HasValue && subtitleIdx.Value != _currentSubIdx) // Found a new segment we should be in
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
                UpdateSubtitleDisplay();
                return;
            }

            if (_state == "playing_segment")
            {
                UpdateSubtitleDisplay();
                if (currentMs >= _segmentEndMs)
                {
                    _currentRepetition++;
                    videoElement.Pause();
                    _isPlaying = false;

                    if (_currentRepetition < CfgRepetitions)
                    {
                        _state = "waiting_pause";
                        Console.WriteLine($"Pausing before repeating segment {_currentSubIdx + 1} / {_subtitleData.Count}");
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CfgDelayMs) };
                        timer.Tick += (s, args) => { AfterRepetitionPause(timer); };
                        timer.Start();
                    }
                    else
                    {
                        _state = "waiting_pause";
                        Console.WriteLine($"Pausing after completing segment {_currentSubIdx + 1} / {_subtitleData.Count}");
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(0) }; // Minimal delay
                        timer.Tick += (s, args) => { AfterSegmentPause(timer); };
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
                if (segment.StartMs <= currentMs && currentMs <= (segment.EndMs + _subtitleExtraDurationMs)) // Consider extra duration for display
                {
                    return i;
                }
            }
            // If past the last subtitle's end time, but still within the video
            if (_subtitleData.Count > 0 && currentMs > _subtitleData.Last().EndMs + _subtitleExtraDurationMs)
            {
                return null; // Or indicate "after last subtitle" if needed elsewhere
            }
            return null;
        }


        private void AfterRepetitionPause(DispatcherTimer timer)
        {
            timer.Stop();
            if (_state == "waiting_pause")
            {
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
                videoElement.Play(); // Continue from current position
                _isPlaying = true;
                _state = "playing_normal";
                _inSubtitleSegment = false; // No longer in a specific repeating segment
                Console.WriteLine($"Continuing normal playback after segment {_currentSubIdx + 1}");

                // If we finished the last segment, currentSubIdx might need to reflect "past last segment" for saving
                // Or we save the index of the next segment to start, if applicable.
                // For now, SaveCurrentSegment is called when entering a segment.
                // If we want to save "completed up to segment X", that's different.
                // The current save logic saves the *active* segment index.
                // If after the last segment, we might save _subtitleData.Count to indicate completion.
                if (_currentSubIdx == _subtitleData.Count - 1)
                {
                    // Consider if we should advance _currentSubIdx for saving purposes to _subtitleData.Count
                    // SaveCurrentSegment(_videoPath, _subtitleData.Count); // Indicates all segments up to the last one were processed
                }
            }
        }

        private void UpdateSubtitleDisplay()
        {
            if (_state == "waiting_pause") return;

            string text = "";
            string statusInfo = "";
            int displaySubIdx = -1;

            if (_state == "playing_normal")
            {
                int? currentVisibleSubIndex = FindSubtitleAtTime((int)videoElement.Position.TotalMilliseconds);
                if (currentVisibleSubIndex.HasValue)
                {
                    displaySubIdx = currentVisibleSubIndex.Value;
                }
            }
            else // playing_segment or user_paused (while in a segment)
            {
                if (_currentSubIdx >= 0 && _currentSubIdx < _subtitleData.Count)
                {
                    displaySubIdx = _currentSubIdx;
                }
            }

            if (displaySubIdx != -1)
            {
                text = _subtitleData[displaySubIdx].Text;
                statusInfo = $"({displaySubIdx + 1}/{_subtitleData.Count})";
                if (_state == "playing_segment" || (_state == "user_paused" && _inSubtitleSegment))
                {
                    statusInfo += $" [Rep: {_currentRepetition + 1}/{CfgRepetitions}]";
                }
            }
            else if (videoElement.Source != null && _currentSubIdx >= _subtitleData.Count && _subtitleData.Count > 0)
            {
                text = "End of subtitles.";
            }
            else if (videoElement.Source == null)
            {
                text = "Open a video file.";
            }
            else
            {
                text = ""; // No subtitle active at current time
            }

            subtitleText.Text = (string.IsNullOrEmpty(statusInfo) ? "" : $" {statusInfo}") + "\n" + text;
        }


        private void JumpToSegment(int segmentIndex)
        {
            if (_subtitleData == null || _subtitleData.Count == 0) return;

            _currentSubIdx = Math.Max(0, Math.Min(segmentIndex, _subtitleData.Count - 1));

            var segment = _subtitleData[_currentSubIdx];
            _segmentStartMs = segment.StartMs;
            _segmentEndMs = segment.EndMs + _subtitleExtraDurationMs;
            _currentRepetition = 0; // Reset repetitions when jumping
            _state = "playing_segment"; // Assume we want repetition logic for the jumped-to segment
            _inSubtitleSegment = true;

            videoElement.Position = TimeSpan.FromMilliseconds(_segmentStartMs);
            if (!_isPlaying) // If paused, play after jumping
            {
                videoElement.Play();
                _isPlaying = true;
            }


            SaveCurrentSegment(_videoPath, _currentSubIdx);
            UpdateSubtitleDisplay();
            Console.WriteLine($"Jumped to segment {_currentSubIdx + 1}");
        }

        private void SaveCurrentSegment(string videoPath, int currentSegmentIndexToSave)
        {
            if (string.IsNullOrEmpty(videoPath)) return;

            OverallProgress overallProgress;
            if (File.Exists(ProgressFile))
            {
                try
                {
                    string json = File.ReadAllText(ProgressFile);
                    overallProgress = JsonSerializer.Deserialize<OverallProgress>(json);
                    if (overallProgress == null) // Handle case where file is empty or just "null"
                    {
                        overallProgress = new OverallProgress();
                    }
                    if (overallProgress.VideoDetails == null) // Ensure dictionary is initialized
                    {
                        overallProgress.VideoDetails = new Dictionary<string, ProgressData>();
                    }
                }
                catch (JsonException)
                {
                    Console.WriteLine($"Warning: {ProgressFile} is corrupted or not valid JSON. Creating a new one.");
                    overallProgress = new OverallProgress();
                }
                catch (Exception ex) // Catch other potential errors during file read/deserialization
                {
                    Console.WriteLine($"Error reading {ProgressFile}: {ex.Message}. Creating a new one.");
                    overallProgress = new OverallProgress();
                }
            }
            else
            {
                overallProgress = new OverallProgress();
            }

            string fileNameKey = Path.GetFileName(videoPath);
            if (!overallProgress.VideoDetails.ContainsKey(fileNameKey))
            {
                overallProgress.VideoDetails[fileNameKey] = new ProgressData();
            }
            overallProgress.VideoDetails[fileNameKey].FullPath = videoPath;
            overallProgress.VideoDetails[fileNameKey].CurrentSegment = currentSegmentIndexToSave;
            overallProgress.LastActiveVideoFileName = fileNameKey;

            try
            {
                string updatedJson = JsonSerializer.Serialize(overallProgress, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProgressFile, updatedJson);
                Console.WriteLine($"Saved progress. Last active video: {fileNameKey}, Segment: {currentSegmentIndexToSave} in {ProgressFile}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not save progress to file: {e.Message}");
            }
        }

        private int LoadLastSegmentForVideo(string videoFileNameKey)
        {
            if (string.IsNullOrEmpty(videoFileNameKey) || !File.Exists(ProgressFile))
                return CfgStartSegment - 1;

            try
            {
                string json = File.ReadAllText(ProgressFile);
                var overallProgress = JsonSerializer.Deserialize<OverallProgress>(json);

                if (overallProgress != null && overallProgress.VideoDetails != null &&
                    overallProgress.VideoDetails.TryGetValue(videoFileNameKey, out ProgressData progressData) &&
                    progressData.CurrentSegment.HasValue)
                {
                    Console.WriteLine($"Loaded segment {progressData.CurrentSegment.Value} for video key {videoFileNameKey}");
                    return progressData.CurrentSegment.Value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load progress for video key {videoFileNameKey} from {ProgressFile}: {ex.Message}");
            }
            return CfgStartSegment - 1;
        }

        private void LoadLastState()
        {
            if (!File.Exists(ProgressFile))
            {
                Console.WriteLine($"{ProgressFile} not found. Prompting for files.");
                return;
            }

            try
            {
                string json = File.ReadAllText(ProgressFile);
                OverallProgress overallProgress = JsonSerializer.Deserialize<OverallProgress>(json);

                if (overallProgress != null && !string.IsNullOrEmpty(overallProgress.LastActiveVideoFileName) &&
                    overallProgress.VideoDetails != null &&
                    overallProgress.VideoDetails.TryGetValue(overallProgress.LastActiveVideoFileName, out ProgressData lastSessionData))
                {
                    string videoFilePathCandidate = lastSessionData.FullPath;
                    // int? segmentToLoad = lastSessionData.CurrentSegment; // This will be handled by LoadVideoAndSubtitles

                    if (string.IsNullOrEmpty(videoFilePathCandidate) || !File.Exists(videoFilePathCandidate))
                    {
                        Console.WriteLine($"Last video file path '{videoFilePathCandidate}' from {ProgressFile} is invalid or file not found. Prompting for files.");
                        return;
                    }

                    string subtitleFileName = Path.GetFileNameWithoutExtension(videoFilePathCandidate) + "_word_merge_translated.srt";
                    string subtitlePathCandidate = Path.Combine(Path.GetDirectoryName(videoFilePathCandidate), subtitleFileName);

                    if (!File.Exists(subtitlePathCandidate))
                    {
                        MessageBox.Show($"Required subtitle file '{subtitleFileName}' for the last session was not found in the directory: \n'{Path.GetDirectoryName(videoFilePathCandidate)}'. \nPlease select files manually.", "Subtitle Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    Console.WriteLine($"Attempting to load last session: Video='{videoFilePathCandidate}', Subtitle='{subtitlePathCandidate}'");
                    // _videoPath will be set by LoadVideoAndSubtitles if successful
                    LoadVideoAndSubtitles(videoFilePathCandidate, subtitlePathCandidate);
                    // If LoadVideoAndSubtitles fails (e.g. user cancels a messagebox, or file is bad), _videoPath might become null.
                    // The check in the constructor `if (string.IsNullOrEmpty(_videoPath) || _subtitleData.Count == 0)` will then call PromptForFiles.
                }
                else
                {
                    Console.WriteLine($"No valid last session data found in {ProgressFile}, or format is incorrect. Prompting for files.");
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Failed to deserialize {ProgressFile}: {ex.Message}. File might be corrupted. Prompting for files.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error loading last state from {ProgressFile}: {ex.Message}. Prompting for files.");
            }
        }


        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsActive || videoElement.Source == null) return; // Only process if window active and video loaded

            if (e.Key == Key.Left)
            {
                if (_subtitleData.Count > 0) JumpToSegment(Math.Max(0, _currentSubIdx - 1));
            }
            else if (e.Key == Key.Right)
            {
                if (_subtitleData.Count > 0) JumpToSegment(Math.Min(_subtitleData.Count - 1, _currentSubIdx + 1));
            }
            else if (e.Key == Key.Space)
            {
                if (_isPlaying)
                {
                    videoElement.Pause();
                    _isPlaying = false;
                    _state = "user_paused"; // Keep track that user paused it
                }
                else
                {
                    videoElement.Play();
                    _isPlaying = true;
                    // Restore state based on whether we were in a segment or normal playback
                    if (_inSubtitleSegment && _currentSubIdx >= 0 && _currentSubIdx < _subtitleData.Count)
                        _state = "playing_segment";
                    else
                        _state = "playing_normal";
                }
                UpdateSubtitleDisplay(); // Update display for pause/play status
            }
            else if (e.Key == Key.O)
            {
                OpenFileButton_Click(this, new RoutedEventArgs()); // Reuse button click logic
            }
            else if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void MediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isPlaying = true;
            // Initial positioning is handled by LoadVideoAndSubtitles
            Console.WriteLine("MediaElement_MediaOpened: Video opened and ready.");
            UpdateSubtitleDisplay(); // Ensure subtitles are correct on open
            if (_timer != null && !_timer.IsEnabled) // Ensure timer starts if not already
            {
                _timer.Start();
            }
        }

        private void MediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            _state = "user_paused"; // Or a new state like "ended"
            subtitleText.Text = "Video ended. Press Space to replay from start or O to open new.";
            // Optionally, save progress indicating video completion
            if (!string.IsNullOrEmpty(_videoPath) && _subtitleData.Count > 0)
            {
                SaveCurrentSegment(_videoPath, _subtitleData.Count); // Save as "past last segment"
            }
        }

        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _isPlaying = false;
            _videoPath = null; // Invalidate current video path
            _subtitleData.Clear();
            subtitleText.Text = "Media failed to load.";
            MessageBox.Show($"Failed to load media: {e.ErrorException.Message}", "Media Error", MessageBoxButton.OK, MessageBoxImage.Error);
            // PromptForFiles(); // Optionally prompt immediately
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!string.IsNullOrEmpty(_videoPath) && videoElement.Source != null)
            {
                // Decide what segment to save: current visual one, or _currentSubIdx (which might be segment being repeated)
                // If video is playing, current position might be between segments if in "playing_normal"
                // For simplicity, saving _currentSubIdx. If it is end of subtitles, it is _subtitleData.Count.
                int segmentToSave = _currentSubIdx;
                if (_state == "playing_normal" && _subtitleData.Count > 0)
                {
                    var currentVisualSeg = FindSubtitleAtTime((int)videoElement.Position.TotalMilliseconds);
                    if (currentVisualSeg.HasValue) segmentToSave = currentVisualSeg.Value;
                    else if (videoElement.Position.TotalMilliseconds >= _subtitleData.Last().EndMs) segmentToSave = _subtitleData.Count; // Past all segments
                }
                SaveCurrentSegment(_videoPath, segmentToSave);
            }

            _timer?.Stop();
            videoElement?.Stop();
            videoElement.Source = null; // Release file lock
        }
    }

    public class SubtitleSegment
    {
        public int StartMs { get; set; }
        public int EndMs { get; set; }
        public string Text { get; set; }
    }

    // New class for the overall progress file structure
    public class OverallProgress
    {
        public string LastActiveVideoFileName { get; set; }
        public Dictionary<string, ProgressData> VideoDetails { get; set; } = new Dictionary<string, ProgressData>();
    }

    public class ProgressData // Modified to include FullPath
    {
        public string FullPath { get; set; } // Full path to the video file
        public int? CurrentSegment { get; set; }
    }
}