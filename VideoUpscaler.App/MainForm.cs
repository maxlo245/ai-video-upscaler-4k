using System.Globalization;

namespace VideoUpscaler.App;

internal sealed class MainForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(13, 17, 24);
    private static readonly Color Card = Color.FromArgb(23, 29, 39);
    private static readonly Color Field = Color.FromArgb(16, 21, 30);
    private static readonly Color TextPrimary = Color.FromArgb(237, 242, 249);
    private static readonly Color TextMuted = Color.FromArgb(151, 163, 181);
    private static readonly Color Accent = Color.FromArgb(88, 166, 255);
    private static readonly Color Green = Color.FromArgb(86, 211, 158);

    private readonly ExternalTools tools = new();
    private readonly TextBox sourceBox = new();
    private readonly TextBox outputBox = new();
    private readonly ComboBox scaleBox = new();
    private readonly ComboBox modelBox = new();
    private readonly Button startButton = new();
    private readonly Button cancelButton = new();
    private readonly Button toolsButton = new();
    private readonly Label dependencyStatus = new();
    private readonly Label jobStatus = new();
    private readonly ProgressBar progress = new();
    private readonly RichTextBox log = new();
    private CancellationTokenSource? cancellation;

    public MainForm()
    {
        Text = "Upscale Studio 4K";
        MinimumSize = new Size(840, 720);
        Size = new Size(1020, 850);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10);
        AllowDrop = true;
        BuildLayout();
        UpdateDependencyStatus();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += (_, _) => cancellation?.Cancel();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Canvas,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Canvas, Padding = new Padding(34, 18, 34, 16) };
        root.Controls.Add(scroll, 0, 1);
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Canvas
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        scroll.Controls.Add(content);

        content.Controls.Add(MakeLabel("VOTRE VIDÉO", 9, TextMuted, true), 0, 0);
        content.Controls.Add(BuildFileCard(), 0, 1);
        content.Controls.Add(BuildSettingsCard(), 0, 2);
        content.Controls.Add(BuildActionArea(), 0, 3);
        content.Controls.Add(BuildLogCard(), 0, 4);

        var footer = new Label
        {
            Dock = DockStyle.Fill,
            Text = "TRAITEMENT LOCAL  ·  VOS VIDÉOS RESTENT SUR VOTRE PC",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8, FontStyle.Bold)
        };
        root.Controls.Add(footer, 0, 2);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(19, 25, 35), Padding = new Padding(34, 20, 34, 16) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(39, 49, 64));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var title = new Label
        {
            Text = "Upscale Studio",
            Font = new Font("Segoe UI Semibold", 24, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(0, 4)
        };
        var mark = new Label
        {
            Text = "4K",
            AutoSize = true,
            BackColor = Color.FromArgb(34, 71, 103),
            ForeColor = Accent,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Padding = new Padding(9, 5, 9, 5),
            Location = new Point(255, 13)
        };
        var subtitle = new Label
        {
            Text = "Agrandissez vos vidéos grâce à l'IA, directement sur votre ordinateur.",
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 10),
            Location = new Point(2, 58)
        };
        header.Controls.Add(title);
        header.Controls.Add(mark);
        header.Controls.Add(subtitle);
        return header;
    }

    private Control BuildFileCard()
    {
        var card = MakeCard();
        var layout = CardLayout(card, 3, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));

        layout.Controls.Add(MakeLabel("Fichier source", 9, TextMuted, true), 0, 0);
        layout.Controls.Add(MakeLabel("Vidéo exportée", 9, TextMuted, true), 0, 1);
        ConfigurePathBox(sourceBox);
        ConfigurePathBox(outputBox);
        layout.Controls.Add(sourceBox, 1, 0);
        layout.Controls.Add(outputBox, 1, 1);
        layout.Controls.Add(MakeButton("Parcourir", BrowseSource), 2, 0);
        layout.Controls.Add(MakeButton("Enregistrer sous", BrowseOutput), 2, 1);
        return card;
    }

    private Control BuildSettingsCard()
    {
        var card = MakeCard();
        var layout = CardLayout(card, 3, 3);
        layout.ColumnStyles[0].Width = 128;
        layout.ColumnStyles[1].SizeType = SizeType.Percent;
        layout.ColumnStyles[1].Width = 100;
        layout.ColumnStyles[2].Width = 215;
        layout.ColumnStyles[2].Width = 215;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));

        layout.Controls.Add(MakeLabel("Agrandissement", 10, TextPrimary), 0, 0);
        ConfigureCombo(scaleBox, ["2×", "4× · jusqu'à 4K"]);
        scaleBox.SelectedIndex = 1;
        layout.Controls.Add(scaleBox, 1, 0);

        layout.Controls.Add(MakeLabel("Style IA", 10, TextPrimary), 0, 1);
        ConfigureCombo(modelBox, ["Réaliste · Real-ESRGAN x4plus", "Animation · Real-ESRGAN x4plus Anime"]);
        layout.Controls.Add(modelBox, 1, 1);
        modelBox.SelectedIndex = 0;

        var note = MakeLabel("La sortie est limitée au 4K UHD (3840 × 2160).", 9, TextMuted);
        layout.Controls.Add(note, 0, 2);
        layout.SetColumnSpan(note, 3);
        return card;
    }

    private Control BuildActionArea()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = Canvas,
            Padding = new Padding(0, 8, 0, 0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));

        ConfigureButton(startButton, "LANCER L'UPSCALE", Accent, Color.FromArgb(12, 24, 37));
        startButton.Dock = DockStyle.Fill;
        startButton.Click += async (_, _) => await StartUpscaleAsync();
        ConfigureButton(cancelButton, "ANNULER", Card, TextMuted);
        cancelButton.Dock = DockStyle.Fill;
        cancelButton.Enabled = false;
        cancelButton.Click += (_, _) => cancellation?.Cancel();

        toolsButton.Text = "Configurer l'IA";
        toolsButton.AutoSize = true;
        toolsButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        toolsButton.FlatStyle = FlatStyle.Flat;
        toolsButton.FlatAppearance.BorderSize = 0;
        toolsButton.ForeColor = Accent;
        toolsButton.BackColor = Canvas;
        toolsButton.Font = new Font("Segoe UI", 9);
        toolsButton.Click += async (_, _) => await InstallToolsAsync();

        dependencyStatus.Text = "Vérification des outils…";
        dependencyStatus.ForeColor = TextMuted;
        dependencyStatus.Dock = DockStyle.Fill;
        dependencyStatus.TextAlign = ContentAlignment.MiddleLeft;
        dependencyStatus.Font = new Font("Segoe UI", 9);

        row.Controls.Add(startButton, 0, 0);
        row.Controls.Add(cancelButton, 1, 0);
        row.Controls.Add(toolsButton, 2, 0);
        row.Controls.Add(dependencyStatus, 0, 1);
        row.SetColumnSpan(dependencyStatus, 3);
        progress.Dock = DockStyle.Fill;
        progress.Style = ProgressBarStyle.Blocks;
        progress.Visible = true;
        progress.ForeColor = Accent;
        row.Controls.Add(progress, 0, 2);
        row.SetColumnSpan(progress, 3);
        return row;
    }

    private Control BuildLogCard()
    {
        var card = MakeCard();
        var layout = CardLayout(card, 1, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        jobStatus.Text = "JOURNAL DE TRAITEMENT";
        jobStatus.Dock = DockStyle.Fill;
        jobStatus.ForeColor = TextMuted;
        jobStatus.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        layout.Controls.Add(jobStatus, 0, 0);

        log.Dock = DockStyle.Fill;
        log.ReadOnly = true;
        log.BorderStyle = BorderStyle.None;
        log.BackColor = Field;
        log.ForeColor = Color.FromArgb(187, 201, 219);
        log.Font = new Font("Consolas", 9);
        log.DetectUrls = false;
        layout.Controls.Add(log, 0, 1);
        AppendLog("Prêt. Choisissez une vidéo pour commencer.");
        return card;
    }

    private static TableLayoutPanel CardLayout(Panel card, int columns, int rows)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Card
        };
        if (columns == 3)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134));
        }
        else
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        }
        card.Controls.Add(layout);
        return layout;
    }

    private static Panel MakeCard()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 4, 0, 10), Padding = new Padding(0) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(37, 46, 59));
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private static Label MakeLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private static void ConfigurePathBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Field;
        box.ForeColor = TextPrimary;
        box.Font = new Font("Segoe UI", 9);
        box.Margin = new Padding(0, 8, 10, 8);
    }

    private static void ConfigureCombo(ComboBox combo, string[] values)
    {
        combo.Dock = DockStyle.Fill;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Field;
        combo.ForeColor = TextPrimary;
        combo.Font = new Font("Segoe UI", 9);
        combo.Margin = new Padding(0, 6, 16, 6);
        combo.Items.AddRange(values);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9),
            Margin = new Padding(0, 5, 0, 5),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(55, 67, 84);
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => action();
        return button;
    }

    private static void ConfigureButton(Button button, string text, Color backColor, Color foreground)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = foreground;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    private void BrowseSource()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choisir une vidéo",
            Filter = "Vidéos|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.mpg;*.mpeg|Tous les fichiers|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        sourceBox.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(outputBox.Text) ||
            string.Equals(outputBox.Text, sourceBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            outputBox.Text = Path.Combine(
                Path.GetDirectoryName(dialog.FileName)!,
                $"{Path.GetFileNameWithoutExtension(dialog.FileName)}_4K.mp4");
        }
    }

    private void BrowseOutput()
    {
        var folder = File.Exists(sourceBox.Text) ? Path.GetDirectoryName(sourceBox.Text) : null;
        using var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la vidéo agrandie",
            Filter = "Vidéo MP4|*.mp4",
            DefaultExt = "mp4",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = folder
        };
        if (!string.IsNullOrWhiteSpace(outputBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(outputBox.Text);
            dialog.FileName = Path.GetFileName(outputBox.Text);
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
            outputBox.Text = dialog.FileName;
    }

    private async Task StartUpscaleAsync()
    {
        var input = sourceBox.Text.Trim();
        var output = outputBox.Text.Trim();
        if (!File.Exists(input))
        {
            MessageBox.Show(this, "Choisissez d'abord un fichier vidéo existant.", "Vidéo manquante",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            BrowseOutput();
            output = outputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
                return;
        }
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Le fichier de sortie doit être différent du fichier source.", "Chemin invalide",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        cancellation = new CancellationTokenSource();
        IProgress<string> progress = new Progress<string>(AppendLog);
        IProgress<string> update = new Progress<string>(message => jobStatus.Text = message.ToUpperInvariant());
        try
        {
            AppendLog("Vérification des moteurs requis…");
            await tools.EnsureInstalledAsync(message => progress.Report(message), cancellation.Token);
            UpdateDependencyStatus();
            var multiplier = scaleBox.SelectedIndex == 0 ? 2 : 4;
            var anime = modelBox.SelectedIndex == 1;
            await new VideoProcessor(tools).RunAsync(
                input, output, multiplier, anime, message => progress.Report(message), cancellation.Token);
            AppendLog("Upscaling terminé.");
            update.Report("TERMINÉ");
            MessageBox.Show(this, $"La vidéo agrandie a été enregistrée ici :\n{output}",
                "Upscaling terminé", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Traitement annulé.");
            update.Report("ANNULÉ");
        }
        catch (Exception exception)
        {
            AppendLog("Erreur : " + exception.Message);
            update.Report("ÉCHEC");
            MessageBox.Show(this, exception.Message, "Upscaling impossible",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            SetBusy(false);
            UpdateDependencyStatus();
        }
    }

    private async Task InstallToolsAsync()
    {
        SetBusy(true);
        cancellation = new CancellationTokenSource();
        IProgress<string> progress = new Progress<string>(AppendLog);
        try
        {
            await tools.EnsureInstalledAsync(message => progress.Report(message), cancellation.Token);
            UpdateDependencyStatus();
            AppendLog("Les moteurs IA et vidéo sont prêts.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Installation des moteurs annulée.");
        }
        catch (Exception exception)
        {
            AppendLog("Installation impossible : " + exception.Message);
            MessageBox.Show(this, exception.Message, "Installation des moteurs impossible",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            SetBusy(false);
            UpdateDependencyStatus();
        }
    }

    private void SetBusy(bool busy)
    {
        startButton.Enabled = !busy;
        toolsButton.Enabled = !busy;
        cancelButton.Enabled = busy;
        scaleBox.Enabled = !busy;
        modelBox.Enabled = !busy;
        sourceBox.Enabled = !busy;
        outputBox.Enabled = !busy;
        progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        progress.MarqueeAnimationSpeed = busy ? 25 : 0;
        jobStatus.Text = busy ? "TRAITEMENT EN COURS" : "JOURNAL DE TRAITEMENT";
        if (!busy)
        {
            progress.Value = 0;
            dependencyStatus.Parent?.Invalidate();
        }
    }

    private void UpdateDependencyStatus()
    {
        var ready = tools.IsReady;
        dependencyStatus.Text = ready
            ? "● Moteur IA prêt · traitement local"
            : "○ Les moteurs seront installés automatiquement au premier usage.";
        dependencyStatus.ForeColor = ready ? Green : TextMuted;
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || log.IsDisposed)
            return;

        if (log.InvokeRequired)
        {
            if (log.IsHandleCreated)
                log.BeginInvoke((Action)(() => AppendLog(message)));
            return;
        }

        var line = message.Replace("\r", " ").Replace("\n", " ");
        if (line.Length > 240)
            line = line[..240] + "…";
        log.AppendText($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}{Environment.NewLine}");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
        if (log.TextLength > 18000)
            log.Text = log.Text[^12000..];
    }

    private void OnDragEnter(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.Effect = eventArgs.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs eventArgs)
    {
        if (eventArgs.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files ||
            !File.Exists(files[0]))
            return;
        sourceBox.Text = files[0];
        outputBox.Text = Path.Combine(
            Path.GetDirectoryName(files[0])!,
            $"{Path.GetFileNameWithoutExtension(files[0])}_4K.mp4");
    }
}
