using NAudio.CoreAudioApi;

namespace ZoomRecorder;

public sealed class MainForm : Form
{
    private readonly ComboBox _cmbMic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly ComboBox _cmbSpeaker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly TextBox _txtOutputFolder = new() { Width = 320 };
    private readonly Button _btnBrowse = new() { Text = "参照...", Width = 80 };
    private readonly Button _btnStart = new() { Text = "録音開始", Width = 120, Height = 36 };
    private readonly Button _btnStop = new() { Text = "録音停止", Width = 120, Height = 36, Enabled = false };
    private readonly Label _lblStatus = new() { Text = "待機中", AutoSize = true };
    private readonly Label _lblFile = new() { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };

    private readonly AudioRecorder _recorder = new();
    private readonly MMDeviceEnumerator _enumerator = new();

    public MainForm()
    {
        Text = "Zoom通話レコーダー";
        Width = 560;
        Height = 340;
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
        var lblMic = new Label { Text = "マイク(自分の声):", AutoSize = true, Left = 16, Top = 20 };
        _cmbMic.Left = 16;
        _cmbMic.Top = 44;

        var lblSpeaker = new Label { Text = "スピーカー出力(相手の声・録音対象):", AutoSize = true, Left = 16, Top = 80 };
        _cmbSpeaker.Left = 16;
        _cmbSpeaker.Top = 104;

        var lblFolder = new Label { Text = "保存先フォルダ:", AutoSize = true, Left = 16, Top = 140 };
        _txtOutputFolder.Left = 16;
        _txtOutputFolder.Top = 164;
        _btnBrowse.Left = 344;
        _btnBrowse.Top = 162;

        _btnStart.Left = 16;
        _btnStart.Top = 210;
        _btnStop.Left = 150;
        _btnStop.Top = 210;

        _lblStatus.Left = 16;
        _lblStatus.Top = 260;
        _lblFile.Left = 16;
        _lblFile.Top = 284;

        Controls.AddRange(new Control[]
        {
            lblMic, _cmbMic, lblSpeaker, _cmbSpeaker, lblFolder, _txtOutputFolder, _btnBrowse,
            _btnStart, _btnStop, _lblStatus, _lblFile
        });
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

        var fileName = $"zoom_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
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
