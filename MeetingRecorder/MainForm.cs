using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MeetingRecorder;

public sealed class MainForm : Form
{
    private readonly ComboBox _cmbMic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _cmbSpeaker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly TextBox _txtOutputFolder = new() { Width = 360 };
    private readonly Button _btnBrowse = new() { Text = "参照...", Width = 80 };
    private readonly Button _btnStart = new() { Text = "録音開始", Width = 130, Height = 36 };
    private readonly Button _btnStop = new() { Text = "録音停止", Width = 130, Height = 36, Enabled = false };
    private readonly Label _lblStatus = new() { Text = "待機中", AutoSize = true };
    private readonly Label _lblFile = new() { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _btnOpenFile = new() { Text = "録音ファイルを開く", Width = 150, Height = 30, Enabled = false };
    private readonly Button _btnOpenFolder = new() { Text = "フォルダを開く", Width = 150, Height = 30, Enabled = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };

    private readonly AudioRecorder _recorder = new();
    private readonly MMDeviceEnumerator _enumerator = new();

    public MainForm()
    {
        Text = "MeetingRecorder";
        Width = 640;
        Height = 480;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadDevices();

        _txtOutputFolder.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ZoomRecordings");

        _btnBrowse.Click += (_, _) => BrowseFolder();
        _btnStart.Click += (_, _) => StartRecording();
        _btnStop.Click += (_, _) => StopRecording();
        _uiTimer.Tick += (_, _) => UpdateStatus();

        _btnOpenFile.Click += (_, _) => OpenRecordedFile();
        _btnOpenFolder.Click += (_, _) => OpenContainingFolder();

        _recorder.ErrorOccurred += (_, ex) =>
        {
            BeginInvoke(() =>
            {
                MessageBox.Show(this, $"録音中にエラーが発生しました:\n{ex.Message}", "エラー",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                StopRecording();
            });
        };

        FormClosing += (_, e) =>
        {
            if (_recorder.IsRecording)
                StopRecording();
        };
    }

    private void BuildLayout()
    {
        const int left = 20;

        var lblMic = new Label { Text = "マイク(自分の声):", AutoSize = true, Left = left, Top = 20 };
        _cmbMic.Left = left;
        _cmbMic.Top = 44;

        var lblSpeaker = new Label { Text = "スピーカー出力(相手の声・録音対象):", AutoSize = true, Left = left, Top = 84 };
        _cmbSpeaker.Left = left;
        _cmbSpeaker.Top = 108;

        var lblFolder = new Label { Text = "保存先フォルダ:", AutoSize = true, Left = left, Top = 148 };
        _txtOutputFolder.Left = left;
        _txtOutputFolder.Top = 172;
        _btnBrowse.Left = left + _txtOutputFolder.Width + 12;
        _btnBrowse.Top = 170;

        _btnStart.Left = left;
        _btnStart.Top = 220;
        _btnStop.Left = left + _btnStart.Width + 16;
        _btnStop.Top = 220;

        _lblStatus.Left = left;
        _lblStatus.Top = 270;
        _lblFile.Left = left;
        _lblFile.Top = 296;

        _btnOpenFile.Left = left;
        _btnOpenFile.Top = 326;
        _btnOpenFolder.Left = left + _btnOpenFile.Width + 12;
        _btnOpenFolder.Top = 326;

        Controls.AddRange(new Control[]
        {
            lblMic, _cmbMic, lblSpeaker, _cmbSpeaker, lblFolder, _txtOutputFolder, _btnBrowse,
            _btnStart, _btnStop, _lblStatus, _lblFile, _btnOpenFile, _btnOpenFolder
        });
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
            _txtOutputFolder.Text = dialog.SelectedPath;
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

        try
        {
            _recorder.Start(fullPath, micItem.Device, speakerItem.Device);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"録音を開始できませんでした:\n{ex.Message}", "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _lblFile.Text = fullPath;
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _cmbMic.Enabled = false;
        _cmbSpeaker.Enabled = false;
        _txtOutputFolder.Enabled = false;
        _btnBrowse.Enabled = false;
        _btnOpenFile.Enabled = false;
        _btnOpenFolder.Enabled = false;
        _uiTimer.Start();
        UpdateStatus();
    }

    private void StopRecording()
    {
        _uiTimer.Stop();
        _recorder.Stop();

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _cmbMic.Enabled = true;
        _cmbSpeaker.Enabled = true;
        _txtOutputFolder.Enabled = true;
        _btnBrowse.Enabled = true;
        _btnOpenFile.Enabled = true;
        _btnOpenFolder.Enabled = true;
        _lblStatus.Text = "停止しました";
    }

    private void UpdateStatus()
    {
        _lblStatus.Text = _recorder.IsRecording
            ? $"● 録音中 {_recorder.Elapsed:hh\\:mm\\:ss}"
            : "待機中";
    }

    private sealed class DeviceItem
    {
        public MMDevice Device { get; }
        public DeviceItem(MMDevice device) => Device = device;
        public override string ToString() => Device.FriendlyName;
    }
}
