using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MeetingRecorder;

public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabRecord = new("録音");
    private readonly TabPage _tabSettings = new("設定");

    private readonly ComboBox _cmbMic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _cmbSpeaker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ProgressBar _meterMic = new() { Width = 420, Height = 14, Minimum = 0, Maximum = 100 };
    private readonly ProgressBar _meterSpeaker = new() { Width = 420, Height = 14, Minimum = 0, Maximum = 100 };
    private readonly Button _btnStart = new() { Text = "録音開始", Width = 130, Height = 36 };
    private readonly Button _btnStop = new() { Text = "録音停止", Width = 130, Height = 36, Enabled = false };
    private readonly Label _lblStatus = new() { Text = "待機中", AutoSize = true };
    private readonly Label _lblFile = new() { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _btnOpenFile = new() { Text = "録音ファイルを開く", Width = 150, Height = 30, Enabled = false };
    private readonly Button _btnOpenFolder = new() { Text = "フォルダを開く", Width = 150, Height = 30, Enabled = false };
    private readonly Button _btnTranscribe = new() { Text = "文字起こしを再作成", Width = 160, Height = 30, Enabled = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };

    private readonly TextBox _txtOutputFolder = new() { Width = 360 };
    private readonly Button _btnBrowse = new() { Text = "参照...", Width = 80 };
    private readonly CheckBox _chkAutoTranscribe = new()
    {
        Text = "録音停止後に自動でWhisper(small)によるローカル文字起こしを行う",
        AutoSize = true,
    };

    private readonly AudioRecorder _recorder = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AudioLevelMonitor _micMonitor = new();
    private readonly AudioLevelMonitor _speakerMonitor = new();

    public MainForm()
    {
        Text = "MeetingRecorder";
        Width = 660;
        Height = 560;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        BuildRecordTab();
        BuildSettingsTab();

        _tabs.TabPages.Add(_tabRecord);
        _tabs.TabPages.Add(_tabSettings);
        Controls.Add(_tabs);

        LoadDevices();
        ApplySettingsToUi();
        RestartMicMonitor();
        RestartSpeakerMonitor();

        _btnBrowse.Click += (_, _) => BrowseFolder();
        _btnStart.Click += (_, _) => StartRecording();
        _btnStop.Click += async (_, _) => await StopRecordingAsync();
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();

        _cmbMic.SelectedIndexChanged += (_, _) => RestartMicMonitor();
        _cmbSpeaker.SelectedIndexChanged += (_, _) => RestartSpeakerMonitor();

        _btnOpenFile.Click += (_, _) => OpenRecordedFile();
        _btnOpenFolder.Click += (_, _) => OpenContainingFolder();
        _btnTranscribe.Click += async (_, _) => await TranscribeAsync();

        _txtOutputFolder.Leave += (_, _) => SaveSettingsFromUi();
        _chkAutoTranscribe.CheckedChanged += (_, _) => SaveSettingsFromUi();

        _recorder.ErrorOccurred += (_, ex) =>
        {
            BeginInvoke(async () =>
            {
                MessageBox.Show(this, $"録音中にエラーが発生しました:\n{ex.Message}", "エラー",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                await StopRecordingAsync();
            });
        };

        FormClosing += (_, e) =>
        {
            if (_recorder.IsRecording)
                _recorder.Stop();
            _micMonitor.Dispose();
            _speakerMonitor.Dispose();
        };
    }

    private void RestartMicMonitor()
    {
        if (_cmbMic.SelectedItem is DeviceItem item)
            _micMonitor.Start(item.Device, loopback: false);
        else
            _micMonitor.Stop();
    }

    private void RestartSpeakerMonitor()
    {
        if (_cmbSpeaker.SelectedItem is DeviceItem item)
            _speakerMonitor.Start(item.Device, loopback: true);
        else
            _speakerMonitor.Stop();
    }

    private void BuildRecordTab()
    {
        const int left = 20;

        var lblMic = new Label { Text = "マイク(自分の声):", AutoSize = true, Left = left, Top = 20 };
        _cmbMic.Left = left;
        _cmbMic.Top = 44;
        _meterMic.Left = left;
        _meterMic.Top = 72;

        var lblSpeaker = new Label { Text = "スピーカー出力(相手の声・録音対象):", AutoSize = true, Left = left, Top = 104 };
        _cmbSpeaker.Left = left;
        _cmbSpeaker.Top = 128;
        _meterSpeaker.Left = left;
        _meterSpeaker.Top = 156;

        _btnStart.Left = left;
        _btnStart.Top = 198;
        _btnStop.Left = left + _btnStart.Width + 16;
        _btnStop.Top = 198;

        _lblStatus.Left = left;
        _lblStatus.Top = 248;
        _lblFile.Left = left;
        _lblFile.Top = 274;

        _btnOpenFile.Left = left;
        _btnOpenFile.Top = 304;
        _btnOpenFolder.Left = left + _btnOpenFile.Width + 12;
        _btnOpenFolder.Top = 304;
        _btnTranscribe.Left = left;
        _btnTranscribe.Top = 344;

        _tabRecord.Controls.AddRange(new Control[]
        {
            lblMic, _cmbMic, _meterMic, lblSpeaker, _cmbSpeaker, _meterSpeaker,
            _btnStart, _btnStop, _lblStatus, _lblFile, _btnOpenFile, _btnOpenFolder, _btnTranscribe
        });
    }

    private void BuildSettingsTab()
    {
        const int left = 20;

        var lblFolder = new Label { Text = "保存先フォルダ:", AutoSize = true, Left = left, Top = 20 };
        _txtOutputFolder.Left = left;
        _txtOutputFolder.Top = 44;
        _btnBrowse.Left = left + _txtOutputFolder.Width + 12;
        _btnBrowse.Top = 42;

        _chkAutoTranscribe.Left = left;
        _chkAutoTranscribe.Top = 84;
        _chkAutoTranscribe.MaximumSize = new Size(600, 0);

        _tabSettings.Controls.AddRange(new Control[]
        {
            lblFolder, _txtOutputFolder, _btnBrowse, _chkAutoTranscribe
        });
    }

    private void ApplySettingsToUi()
    {
        _txtOutputFolder.Text = _settings.OutputFolder;
        _chkAutoTranscribe.Checked = _settings.AutoTranscribe;
    }

    private void SaveSettingsFromUi()
    {
        _settings.OutputFolder = _txtOutputFolder.Text;
        _settings.AutoTranscribe = _chkAutoTranscribe.Checked;
        _settings.Save();
    }

    private async Task TranscribeAsync()
    {
        if (string.IsNullOrEmpty(_recorder.OutputPath) || !File.Exists(_recorder.OutputPath))
        {
            MessageBox.Show(this, "録音ファイルが見つかりません。", "確認",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Transcriber.IsModelDownloaded)
        {
            var proceed = MessageBox.Show(this,
                "初回のみ、文字起こし用のWhisperモデル(small, 約500MB)をダウンロードします。\nよろしいですか?",
                "モデルのダウンロード", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (proceed != DialogResult.Yes)
                return;
        }

        var wavPath = _recorder.OutputPath;
        var txtPath = Path.ChangeExtension(wavPath, ".txt");
        var previousStatus = _lblStatus.Text;

        _btnTranscribe.Enabled = false;
        _lblStatus.Text = "文字起こし中... (Whisper small)";

        try
        {
            await Transcriber.TranscribeToTextFileAsync(wavPath, txtPath);
            _lblStatus.Text = "文字起こし完了";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"文字起こしに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = previousStatus;
        }
        finally
        {
            _btnTranscribe.Enabled = true;
        }
    }

    private void OpenRecordedFile()
    {
        if (string.IsNullOrEmpty(_recorder.OutputPath) || !File.Exists(_recorder.OutputPath))
        {
            MessageBox.Show(this, "録音ファイルが見つかりません。", "確認",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(_recorder.OutputPath) { UseShellExecute = true });
    }

    private void OpenContainingFolder()
    {
        if (string.IsNullOrEmpty(_recorder.OutputPath) || !File.Exists(_recorder.OutputPath))
        {
            MessageBox.Show(this, "録音ファイルが見つかりません。", "確認",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_recorder.OutputPath}\"") { UseShellExecute = true });
    }

    private void LoadDevices()
    {
        _cmbMic.Items.Clear();
        _cmbSpeaker.Items.Clear();

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            _cmbMic.Items.Add(new DeviceItem(device));

        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            _cmbSpeaker.Items.Add(new DeviceItem(device));

        if (_cmbMic.Items.Count > 0) _cmbMic.SelectedIndex = 0;
        if (_cmbSpeaker.Items.Count > 0) _cmbSpeaker.SelectedIndex = 0;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(_txtOutputFolder.Text)
                ? _txtOutputFolder.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtOutputFolder.Text = dialog.SelectedPath;
            SaveSettingsFromUi();
        }
    }

    private void StartRecording()
    {
        if (_cmbMic.SelectedItem is not DeviceItem micItem ||
            _cmbSpeaker.SelectedItem is not DeviceItem speakerItem)
        {
            MessageBox.Show(this, "マイクとスピーカーを選択してください。", "確認",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var fileName = $"meeting_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        var fullPath = Path.Combine(_txtOutputFolder.Text, fileName);

        // 監視用の軽量キャプチャを止めてから本番の録音を開始する
        _micMonitor.Stop();
        _speakerMonitor.Stop();

        try
        {
            _recorder.Start(fullPath, micItem.Device, speakerItem.Device);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"録音を開始できませんでした:\n{ex.Message}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            RestartMicMonitor();
            RestartSpeakerMonitor();
            return;
        }

        _lblFile.Text = fullPath;
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _cmbMic.Enabled = false;
        _cmbSpeaker.Enabled = false;
        _btnOpenFile.Enabled = false;
        _btnOpenFolder.Enabled = false;
        _btnTranscribe.Enabled = false;
        _uiTimer.Start();
        UpdateStatus();
    }

    private async Task StopRecordingAsync()
    {
        _uiTimer.Stop();
        _btnStop.Enabled = false;
        _recorder.Stop();

        _btnStart.Enabled = true;
        _cmbMic.Enabled = true;
        _cmbSpeaker.Enabled = true;
        _btnOpenFile.Enabled = true;
        _btnOpenFolder.Enabled = true;
        _btnTranscribe.Enabled = true;
        _lblStatus.Text = "停止しました";

        RestartMicMonitor();
        RestartSpeakerMonitor();

        if (_settings.AutoTranscribe)
            await TranscribeAsync();
    }

    private void UpdateStatus()
    {
        _lblStatus.Text = _recorder.IsRecording
            ? $"● 録音中 {_recorder.Elapsed:hh\\:mm\\:ss}"
            : "待機中";

        _meterMic.Value = LevelToGaugeValue(_micMonitor.Level);
        _meterSpeaker.Value = LevelToGaugeValue(_speakerMonitor.Level);
    }

    private static int LevelToGaugeValue(float peak)
    {
        // ピーク振幅(0..1)をVUメーター風にdBスケール(-50dB〜0dB)へマッピングする
        var db = 20.0 * Math.Log10(Math.Max(peak, 1e-6));
        var normalized = (db + 50.0) / 50.0;
        return (int)Math.Clamp(normalized * 100.0, 0, 100);
    }

    private sealed class DeviceItem
    {
        public MMDevice Device { get; }
        public DeviceItem(MMDevice device) => Device = device;
        public override string ToString() => Device.FriendlyName;
    }
}
