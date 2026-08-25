using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeMote;
using FreeMote.Plugins;
using FreeMote.Psb;
using FreeMote.PsBuild;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Image = System.Windows.Controls.Image;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace PSBPSPDynamicCharacterPlayer;

public partial class MainWindow : Window
{
    class StageSlot
    {
        public string Name = "";
        public string SourcePath = "";
        public string RawPath = "";
        public FreeMote.EmotePlayer? Player;
        public bool Paused;
        public List<TimelineRecord>? SavedTimelines;
    }

    class TimelineRecord
    {
        public string Label = "";
        public FreeMote.TimelinePlayFlags Flags;
    }

    const string DefaultModelRoot = @"D:\test\galgame\Model1";
    string _modelRoot = DefaultModelRoot;

    FreeMote.Emote? _emote;
    FreeMote.EmotePlayer? _player;
    D3DImage? _di;
    IntPtr _scene = IntPtr.Zero;
    bool _playing = true;
    bool _rendering;
    long _lastRenderTimestamp;
    bool _dragging;
    System.Windows.Point _lastDragPoint;
    bool _initialLoaded;
    bool _updatingStageList;
    int _playerCounter;
    int _selectedStage = -1;
    int _dragStartIndex = -1;
    System.Windows.Point _dragStartPoint;
    ListBoxItem? _dropTargetItem;
    System.Windows.Media.Color _customAccent = Color.FromRgb(0x3a, 0x5f, 0x9f);
    bool _themeReady;
    System.Windows.Threading.DispatcherTimer? _startupRefreshTimer;
    bool _freeMountReady;
    readonly Dictionary<string, string> _rawCache = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ListBoxItem> _items = new();
    readonly List<StageSlot> _stage = new();
    readonly List<string> _tempFiles = new();

    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
    }

    void OnContentRendered(object? sender, EventArgs e)
    {
        if (_initialLoaded) return;
        _initialLoaded = true;
        LoadSavedModelRoot();
        LoadSavedTheme();
        _themeReady = true;
        LoadModelList();

        if (_items.Count > 0)
        {
            ModelList.SelectedIndex = 0;
            if (((ListBoxItem)_items[0]).Tag is string path)
            {
                AddStageModel(path);

                _startupRefreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _startupRefreshTimer.Tick += (_, _) =>
                {
                    _startupRefreshTimer?.Stop();
                    _startupRefreshTimer = null;
                    RebuildRendering();
                    StatusText.Text = "启动1秒后已自动刷新清晰度";
                };
                _startupRefreshTimer.Start();
            }
        }
        else
        {
            StatusText.Text = "所选目录中未找到 PSP/PSB 动态立绘";
        }
    }

    static string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model_folder.txt");
    static string ThemeSettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme.txt");

    void LoadSavedModelRoot()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = File.ReadAllText(SettingsPath).Trim();
                if (Directory.Exists(saved)) _modelRoot = saved;
            }
        }
        catch { }
    }

    void SaveModelRoot()
    {
        try { File.WriteAllText(SettingsPath, _modelRoot); } catch { }
    }

    void LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(ThemeSettingsPath)) return;
            var lines = File.ReadAllLines(ThemeSettingsPath);
            var themeName = lines.Length > 0 ? lines[0].Trim() : "深蓝";

            if (themeName == "自定义" && lines.Length >= 2)
            {
                try
                {
                    _customAccent = (Color)System.Windows.Media.ColorConverter.ConvertFromString(lines[1].Trim());
                }
                catch { }
                EnsureCustomThemeItem();
                ApplyTheme("自定义");
                return;
            }

            foreach (var obj in ThemeCombo.Items)
            {
                if (obj is ComboBoxItem ci && (string?)ci.Content == themeName)
                {
                    ThemeCombo.SelectedItem = ci;
                    return;
                }
            }
        }
        catch { }
    }

    void EnsureCustomThemeItem()
    {
        bool has = false;
        foreach (var obj in ThemeCombo.Items)
        {
            if (obj is ComboBoxItem ci && (string?)ci.Content == "自定义")
            {
                has = true;
                ThemeCombo.SelectedItem = ci;
                break;
            }
        }
        if (!has)
        {
            var custom = new ComboBoxItem { Content = "自定义" };
            ThemeCombo.Items.Add(custom);
            ThemeCombo.SelectedItem = custom;
        }
    }

    void SaveTheme(string themeName)
    {
        try
        {
            var lines = new List<string> { themeName };
            if (themeName == "自定义") lines.Add(_customAccent.ToString());
            File.WriteAllLines(ThemeSettingsPath, lines);
        }
        catch { }
    }

    void LoadModelList()
    {
        ModelList.Items.Clear();
        _items.Clear();
        FolderPathText.Text = _modelRoot;
        if (!Directory.Exists(_modelRoot))
        {
            StatusText.Text = "目录不存在: " + _modelRoot;
            return;
        }

        var files = Directory.GetFiles(_modelRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in files)
        {
            var ext = Path.GetExtension(p);
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var jfi = new FileInfo(p);
                if (jfi.Length < 10 * 1024) continue;
                continue;
            }

            bool isPsp = ext.Equals(".psp", StringComparison.OrdinalIgnoreCase);
            bool isPsb = ext.Equals(".psb", StringComparison.OrdinalIgnoreCase);
            if (!isPsp && !isPsb) continue;

            // 隐藏软件自动生成的“PS4 -> Win”转换产物，避免模型库出现重复
            if (isPsb && IsConvertedPsb(p)) continue;

            var fi = new FileInfo(p);
            if (fi.Length < 10 * 1024) continue;

            var item = new ListBoxItem { Content = Path.GetFileNameWithoutExtension(p), Tag = p, ToolTip = p };
            _items.Add(item);
            ModelList.Items.Add(item);
        }
    }

    static bool IsConvertedPsb(string path)
    {
        if (!Path.GetExtension(path).Equals(".psb", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(".ps4.win", StringComparison.OrdinalIgnoreCase);
    }


    void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is ListBoxItem { Tag: string path })
            StatusText.Text = "模型库选择: " + Path.GetFileName(path);
    }

    void StageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingStageList) return;
        if (StageList.SelectedItem is ListBoxItem { Tag: int idx }) SelectStage(idx);
    }

    void ModelList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ModelList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.Tag is string path) AddStageModel(path);
        e.Handled = true;
    }

    void StageList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(StageList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.Tag is int idx && idx >= 0 && idx < _stage.Count)
        {
            _selectedStage = idx;
            RemoveSelectedStageModel();
            e.Handled = true;
        }
    }

    void StageList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(StageList);
        var item = ItemsControl.ContainerFromElement(StageList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _dragStartIndex = item != null ? StageList.ItemContainerGenerator.IndexFromContainer(item) : -1;
    }

    void StageList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragStartIndex < 0) return;
        var pos = e.GetPosition(StageList);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop(StageList, new DataObject("StageMove", _dragStartIndex), DragDropEffects.Move);
        _dragStartIndex = -1;
    }

    void StageList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var targetItem = ItemsControl.ContainerFromElement(StageList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (_dropTargetItem != null && _dropTargetItem != targetItem) ResetDropTarget();
        if (targetItem != null && targetItem != _dropTargetItem)
        {
            _dropTargetItem = targetItem;
            targetItem.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x8a, 0xd0));
            targetItem.BorderBrush = new SolidColorBrush(Color.FromRgb(0xaa, 0xd0, 0xff));
            targetItem.BorderThickness = new Thickness(2);
        }
    }

    void StageList_DragLeave(object sender, DragEventArgs e) => ResetDropTarget();

    void ResetDropTarget()
    {
        if (_dropTargetItem == null) return;
        _dropTargetItem.ClearValue(Control.BackgroundProperty);
        _dropTargetItem.ClearValue(Control.BorderBrushProperty);
        _dropTargetItem.ClearValue(Control.BorderThicknessProperty);
        _dropTargetItem = null;
    }

    void StageList_Drop(object sender, DragEventArgs e)
    {
        ResetDropTarget();
        if (!e.Data.GetDataPresent("StageMove")) return;

        int src = (int)e.Data.GetData("StageMove");
        var targetItem = ItemsControl.ContainerFromElement(StageList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        int dst = targetItem != null ? StageList.ItemContainerGenerator.IndexFromContainer(targetItem) : _stage.Count - 1;

        if (src < 0 || src >= _stage.Count || dst < 0 || dst >= _stage.Count || src == dst) return;

        var slot = _stage[src];
        _stage.RemoveAt(src);
        _stage.Insert(dst, slot);

        _selectedStage = dst;
        RefreshStageList();
        RebuildRendering();
        SelectStage(dst);
        StatusText.Text = "图层顺序已调整";
        e.Handled = true;
    }

    void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is ListBoxItem { Tag: string path }) AddStageModel(path);
        else StatusText.Text = "请先在模型库中选择要开启的模型";
    }

    void CloseBtn_Click(object sender, RoutedEventArgs e) => RemoveSelectedStageModel();

    void AddStageModel(string path)
    {
        try
        {
            EnsureEmote();
            var slot = new StageSlot { Name = "Chara" + (++_playerCounter), SourcePath = path };
            var raw = GetOrPrepareRaw(path);
            slot.RawPath = raw;
            var player = _emote!.CreatePlayer(slot.Name, raw);
            if (player == null) throw new InvalidOperationException("FreeMote 未能创建玩家实例：" + path);

            player.SetScale(1f, 0f, 0f);
            player.SetCoord(0f, 0f, 0f, 0f);
            player.SetVariable("fade_z", 256f, 0f, 0f);
            player.SetSmoothing(true);
            player.Show();
            player.SetCoord(_stage.Count * 130f, 0f, 0f, 0f);
            slot.Player = player;

            _stage.Add(slot);
            RefreshStageList();
            SelectStage(_stage.Count - 1);
            StatusText.Text = Path.GetFileNameWithoutExtension(raw).EndsWith(".ps4.win", StringComparison.OrdinalIgnoreCase)
                ? "检测到 PS4，转换为 Win 并开启: " + Path.GetFileName(path)
                : "已开启: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "开启失败: " + ex.Message;
        }
    }

    void EnsureEmote()
    {
        if (_emote != null) return;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _emote = new FreeMote.Emote(hwnd, Math.Max(640, (int)StageLayer.ActualWidth), Math.Max(480, (int)StageLayer.ActualHeight), true);
        _emote.EmoteInit();

        _di = new D3DImage();
        Viewport.Source = _di;
        _di.IsFrontBufferAvailableChanged += (_, _) =>
        {
            if (_di != null && _di.IsFrontBufferAvailable) BeginRenderingScene();
        };
        BeginRenderingScene();
    }

    void BeginRenderingScene()
    {
        if (!_di.IsFrontBufferAvailable || _emote == null) return;

        _scene = _emote.D3DSurface;
        if (_scene == IntPtr.Zero) return;

        _di.Lock();
        try { _di.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _scene); }
        finally { _di.Unlock(); }

        if (!_rendering)
        {
            _rendering = true;
            _lastRenderTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }
    }

    void RemoveSelectedStageModel()
    {
        if (_selectedStage < 0 || _selectedStage >= _stage.Count) return;

        var slot = _stage[_selectedStage];
        if (_player == slot.Player) _player = null;
        try { _emote?.DeletePlayer(slot.Player); } catch { }

        var raw = slot.RawPath;
        _stage.RemoveAt(_selectedStage);
        _selectedStage = -1;

        if (!_stage.Any(s => !string.IsNullOrEmpty(s.RawPath) && string.Equals(s.RawPath, raw, StringComparison.OrdinalIgnoreCase)))
        {
            // 只删除运行时生成的临时文件；PS4 转出来的 Win 文件保留在原文件旁边
            if (!string.IsNullOrEmpty(raw) && _tempFiles.Contains(raw))
            {
                try { File.Delete(raw); } catch { }
                _tempFiles.Remove(raw);
            }
            var staleKeys = _rawCache.Where(kv => string.Equals(kv.Value, raw, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys) _rawCache.Remove(key);
        }

        if (_stage.Count == 0)
        {
            _player = null;
            RefreshStageList();
            BuildTimelineButtons();
            StatusText.Text = "舞台已清空";
        }
        else
        {
            var next = Math.Min(_selectedStage, _stage.Count - 1);
            SelectStage(next);
        }
    }

    void RefreshStageList()
    {
        _updatingStageList = true;
        try
        {
            StageList.Items.Clear();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _stage.Count; i++)
            {
                var key = _stage[i].SourcePath;
                counts.TryGetValue(key, out int cnt);
                cnt++;
                counts[key] = cnt;

                var baseName = Path.GetFileNameWithoutExtension(_stage[i].SourcePath);
                var label = cnt > 1 ? $"{baseName} #{cnt}" : baseName;
                var item = new ListBoxItem { Content = $"{i + 1}. " + label, Tag = i, ToolTip = _stage[i].SourcePath };
                StageList.Items.Add(item);
            }

            if (_selectedStage >= 0 && _selectedStage < _stage.Count)
                StageList.SelectedIndex = _selectedStage;
        }
        finally
        {
            _updatingStageList = false;
        }
    }

    void SelectStage(int idx)
    {
        if (idx < 0 || idx >= _stage.Count) return;
        _selectedStage = idx;
        _player = _stage[idx].Player;

        RefreshStageList();
        BuildTimelineButtons();
        ApplyZoom((float)ZoomSlider.Value);
        StatusText.Text = "当前模型: " + Path.GetFileName(_stage[idx].SourcePath);
    }

    string PreparePsb(string pspPath, out bool persistent)
    {
        persistent = false;
        if (!_freeMountReady)
        {
            FreeMount.Init(null);
            _freeMountReady = true;
        }
        var ctx = FreeMount.CreateContext(null);
        using var file = File.OpenRead(pspPath);

        string? shellName = null;
        var unpacked = ctx.OpenFromShell(file, ref shellName);
        Stream psbStream = unpacked ?? (Stream)file;
        var psb = new PSB(psbStream, true, null);

        // PS4 平台的纹理（RGBA4444_SW 等）FreeMote 原生播放器不能直接渲染，
        // 这里自动转成 Win 平台的普通像素格式（RGBA8）并保存到原文件旁边（hello.ps4.psb -> hello.ps4.win.psb）。
        if (psb.Platform == PsbSpec.ps4)
        {
            ConvertPs4TexturesToWin(psb);
            try { PsbExtension.FixMotionMetadata(psb); } catch { }

            psb.Merge(false, false);
            var output = Path.ChangeExtension(pspPath, $".{PsbSpec.win}{FreeMoteExtension.DefaultExtension(psb.Type)}");
            File.WriteAllBytes(output, psb.Build());
            persistent = true;
            return output;
        }

        if (psb.Platform == PsbSpec.krkr)
            PsbSpecConverter.SwitchSpec(psb, PsbSpec.win, FreeMoteExtension.DefaultPixelFormat(PsbSpec.krkr));

        try { PsbExtension.FixMotionMetadata(psb); } catch { }

        psb.Merge(false, false);
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, psb.Build());
        return tmp;
    }


    static void ConvertPs4TexturesToWin(PSB psb)
    {
        var targetFormat = FreeMoteExtension.DefaultPixelFormat(PsbSpec.win);
        var targetType = FreeMoteExtension.ToStringForPsb(targetFormat);
        var metas = PsbResHelper.CollectResources<FreeMote.Psb.ImageMetadata>(psb, false);

        var imageMetas = new List<FreeMote.Psb.ImageMetadata>();
        var bitmaps = new List<System.Drawing.Bitmap>();
        try
        {
            // 必须先按原始 ps4 spec 解码：PS4 的 RGBA4444_SW 实际是 Tile 格式。
            // 如果先 SwitchSpec 到 win，会被当成非 Tile 解码，造成贴图错乱。
            foreach (var meta in metas)
            {
                if (meta.TypeString == null || meta.Width <= 0 || meta.Height <= 0) continue;
                var bmp = meta.ToImage();
                if (bmp == null) continue;
                imageMetas.Add(meta);
                bitmaps.Add(bmp);
            }

            PsbSpecConverter.SwitchSpec(psb, PsbSpec.win, targetFormat);

            for (int i = 0; i < imageMetas.Count; i++)
            {
                var meta = imageMetas[i];
                meta.TypeString.Value = targetType;
                meta.Compress = PsbCompressType.None;
                meta.SetData(bitmaps[i]);
            }
        }
        finally
        {
            foreach (var b in bitmaps) b.Dispose();
        }
    }

    string GetOrPrepareRaw(string source)
    {
        if (_rawCache.TryGetValue(source, out var cached) && File.Exists(cached)) return cached;
        var raw = PreparePsb(source, out bool persistent);
        _rawCache[source] = raw;
        if (!persistent && !_tempFiles.Contains(raw)) _tempFiles.Add(raw);
        return raw;
    }

    void DeleteTempFiles()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        _tempFiles.Clear();
        _rawCache.Clear();
    }

    void BuildTimelineButtons()
    {
        TimelinePanel.Children.Clear();
        if (_player == null)
        {
            TimelinePanel.Children.Add(new TextBlock { Text = "请先开启并选择一个舞台模型", Foreground = Brushes.Gray, Margin = new Thickness(6) });
            return;
        }

        uint mainCount = _player.CountMainTimelines();
        uint diffCount = _player.CountDiffTimelines();

        if (mainCount > 0)
        {
            TimelinePanel.Children.Add(new TextBlock { Text = "主时间线", Foreground = Brushes.LightSteelBlue, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center });
            for (uint i = 0; i < mainCount; i++) AddTimelineButton(_player.GetMainTimelineLabelAt(i), "main");
        }

        if (diffCount > 0)
        {
            TimelinePanel.Children.Add(new Separator { Width = 2, Margin = new Thickness(8, 4, 8, 4) });
            TimelinePanel.Children.Add(new TextBlock { Text = "差分/表情", Foreground = Brushes.Orange, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center });
            for (uint i = 0; i < diffCount; i++) AddTimelineButton(_player.GetDiffTimelineLabelAt(i), "diff");
        }

        if (mainCount == 0 && diffCount == 0)
            TimelinePanel.Children.Add(new TextBlock { Text = "该 PSP/PSB 没有可切换时间线", Foreground = Brushes.Gray, Margin = new Thickness(6) });
    }

    void AddTimelineButton(string label, string kind)
    {
        var btn = new Button
        {
            Content = label,
            Tag = kind,
            Margin = new Thickness(3),
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent,
            Foreground = kind == "diff" ? Brushes.Orange : Brushes.LightSteelBlue,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x55, 0x77)),
            FontWeight = FontWeights.Bold
        };
        btn.Click += TimelineBtn_Click;
        TimelinePanel.Children.Add(btn);
    }

    void TimelineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null || sender is not Button { Content: string label, Tag: string kind }) return;
        try
        {
            var flags = kind == "diff" ? FreeMote.TimelinePlayFlags.TIMELINE_PLAY_DIFFERENCE : FreeMote.TimelinePlayFlags.NONE;
            _player.PlayTimeline(label, flags);
            StatusText.Text = "播放时间线: " + label + (kind == "diff" ? " (差分)" : "");
        }
        catch (Exception ex) { StatusText.Text = "时间线播放失败: " + ex.Message; }
    }

    void PauseCurrentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedStage < 0 || _selectedStage >= _stage.Count) { StatusText.Text = "请先选择一个模型"; return; }
        var slot = _stage[_selectedStage];
        if (slot.Paused) return;

        var saved = new List<TimelineRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint n = slot.Player!.CountPlayingTimelines();
        for (uint i = 0; i < n; i++)
        {
            try
            {
                var label = slot.Player.GetPlayingTimelineLabelAt(i);
                var flags = (FreeMote.TimelinePlayFlags)slot.Player.GetPlayingTimelineFlagsAt(i);
                if (!seen.Contains(label)) { saved.Add(new TimelineRecord { Label = label, Flags = flags }); seen.Add(label); }
                slot.Player.StopTimeline(label);
            }
            catch { }
        }

        uint mainCount = slot.Player.CountMainTimelines();
        for (uint i = 0; i < mainCount; i++)
        {
            try
            {
                var label = slot.Player.GetMainTimelineLabelAt(i);
                if (!seen.Contains(label)) { saved.Add(new TimelineRecord { Label = label, Flags = FreeMote.TimelinePlayFlags.NONE }); seen.Add(label); slot.Player.StopTimeline(label); }
            }
            catch { }
        }

        uint diffCount = slot.Player.CountDiffTimelines();
        for (uint i = 0; i < diffCount; i++)
        {
            try
            {
                var label = slot.Player.GetDiffTimelineLabelAt(i);
                if (!seen.Contains(label)) { saved.Add(new TimelineRecord { Label = label, Flags = FreeMote.TimelinePlayFlags.TIMELINE_PLAY_DIFFERENCE }); seen.Add(label); slot.Player.StopTimeline(label); }
            }
            catch { }
        }

        slot.SavedTimelines = saved;
        slot.Paused = true;
        StatusText.Text = "已停止当前模型动画";
    }

    void ResumeCurrentBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedStage < 0 || _selectedStage >= _stage.Count) return;
        var slot = _stage[_selectedStage];
        if (!slot.Paused) return;

        if (slot.SavedTimelines != null)
        {
            foreach (var t in slot.SavedTimelines)
            {
                try { slot.Player!.PlayTimeline(t.Label, t.Flags); } catch { }
            }
        }
        slot.SavedTimelines = null;
        slot.Paused = false;
        StatusText.Text = "已恢复当前模型";
    }

    void PauseAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _playing = false;
        StatusText.Text = "已全部暂停";
    }

    void ResumeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _playing = true;
        StatusText.Text = "已全部恢复";
    }

    void PrevBtn_Click(object sender, RoutedEventArgs e) => MoveModel(-1);
    void NextBtn_Click(object sender, RoutedEventArgs e) => MoveModel(1);

    void MoveModel(int delta)
    {
        if (_items.Count == 0) return;
        var idx = ModelList.SelectedIndex;
        if (idx < 0) idx = 0;
        var next = (idx + delta + _items.Count) % _items.Count;
        ModelList.SelectedIndex = next;
    }

    void ChooseFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择包含 .PSP / .PSB 动态立绘的文件夹",
            SelectedPath = Directory.Exists(_modelRoot) ? _modelRoot : DefaultModelRoot,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _modelRoot = dlg.SelectedPath;
            SaveModelRoot();
            LoadModelList();
            if (_items.Count > 0) { ModelList.SelectedIndex = 0; StatusText.Text = "已切换到文件夹: " + _modelRoot; }
            else StatusText.Text = "所选文件夹中没有找到 PSP/PSB 动态立绘";
        }
    }

    void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_modelRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", _modelRoot) { UseShellExecute = true });
    }

    void BgImageBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*" };
        if (dlg.ShowDialog() == true)
        {
            BackgroundVideo.Stop();
            BackgroundVideo.Source = null;
            BackgroundVideo.Visibility = Visibility.Collapsed;
            BackgroundImage.Source = new BitmapImage(new Uri(dlg.FileName));
            BackgroundImage.Visibility = Visibility.Visible;
            StatusText.Text = "背景图片: " + Path.GetFileName(dlg.FileName);
        }
    }

    void BgVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "视频|*.mp4;*.avi;*.wmv;*.mov;*.mkv|所有文件|*.*" };
        if (dlg.ShowDialog() == true)
        {
            BackgroundImage.Source = null;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundVideo.Source = new Uri(dlg.FileName);
            BackgroundVideo.Visibility = Visibility.Visible;
            BackgroundVideo.Play();
            StatusText.Text = "背景视频: " + Path.GetFileName(dlg.FileName);
        }
    }

    void ClearBgBtn_Click(object sender, RoutedEventArgs e)
    {
        BackgroundVideo.Stop();
        BackgroundVideo.Source = null;
        BackgroundVideo.Visibility = Visibility.Collapsed;
        BackgroundImage.Source = null;
        BackgroundImage.Visibility = Visibility.Collapsed;
        StatusText.Text = "背景已清除";
    }

    void RefreshClarityBtn_Click(object sender, RoutedEventArgs e)
    {
        RebuildRendering();
        StatusText.Text = "已刷新清晰度";
    }

    void RebuildRendering()
    {
        try
        {
            int sel = _selectedStage;
            StopRenderingScene();

            if (_emote != null)
            {
                try { _emote.Dispose(); } catch { }
                try { _emote.D3DRelease(); } catch { }
                _emote = null;
            }
            if (_di != null)
            {
                try
                {
                    _di.Lock();
                    try { _di.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
                    finally { _di.Unlock(); }
                }
                catch { }
                _di = null;
            }
            Viewport.Source = null;

            EnsureEmote();
            for (int i = 0; i < _stage.Count; i++)
            {
                var slot = _stage[i];
                var raw = GetOrPrepareRaw(slot.SourcePath);
                slot.RawPath = raw;
                var player = _emote!.CreatePlayer(slot.Name, raw);
                if (player == null) throw new InvalidOperationException("重建模型失败：" + slot.SourcePath);
                player.SetScale(1f, 0f, 0f);
                player.SetCoord(i * 130f, 0f, 0f, 0f);
                player.SetVariable("fade_z", 256f, 0f, 0f);
                player.SetSmoothing(true);
                player.Show();
                slot.Player = player;
            }

            BeginRenderingScene();
            if (sel >= 0 && sel < _stage.Count) SelectStage(sel);
            else BuildTimelineButtons();
        }
        catch (Exception ex)
        {
            StatusText.Text = "重建渲染失败: " + ex.Message;
        }
    }

    void StopRenderingScene()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        _scene = IntPtr.Zero;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (!_di.IsFrontBufferAvailable || _emote == null || _scene == IntPtr.Zero) return;

        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - _lastRenderTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastRenderTimestamp = now;

        try
        {
            _emote.Update(_playing ? (float)elapsedMs : 0f);

            _di.Lock();
            try
            {
                _emote.D3DBeginScene();
                _emote.Draw();
                _emote.D3DEndScene();
                _di.AddDirtyRect(new Int32Rect(0, 0, _di.PixelWidth, _di.PixelHeight));
            }
            finally { _di.Unlock(); }
        }
        catch { }
    }

    void BgModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyBgMode();

    void ApplyBgMode()
    {
        if (BackgroundImage == null || BackgroundVideo == null) return;
        var mode = (BgModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "自适应";
        var stretch = mode switch
        {
            "拉伸" => Stretch.Fill,
            "裁剪" => Stretch.UniformToFill,
            "原始大小" => Stretch.None,
            _ => Stretch.Uniform
        };
        BackgroundImage.Stretch = stretch;
        BackgroundVideo.Stretch = stretch;
    }

    void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = (ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        ApplyTheme(name);
        if (_themeReady && name != null) SaveTheme(name);
    }

    void ApplyTheme(string? theme)
    {
        Color bg, panel, item, accent, header, text, sub, border, hover, borderStrong;

        switch (theme)
        {
            case "深红":
                bg = Color.FromRgb(0x18, 0x0d, 0x10); panel = Color.FromRgb(0x22, 0x12, 0x16); item = Color.FromRgb(0x2a, 0x18, 0x1e);
                accent = Color.FromRgb(0x9f, 0x3a, 0x3a); header = Color.FromRgb(0xff, 0x9e, 0x9e); text = Color.FromRgb(0xf0, 0xe0, 0xe0);
                sub = Color.FromRgb(0xaa, 0x88, 0x88); border = Color.FromRgb(0x55, 0x33, 0x33); hover = Color.FromRgb(0x4d, 0x29, 0x29);
                borderStrong = Color.FromRgb(0xff, 0x7a, 0x7a);
                break;
            case "深绿":
                bg = Color.FromRgb(0x0c, 0x18, 0x0f); panel = Color.FromRgb(0x12, 0x21, 0x16); item = Color.FromRgb(0x16, 0x2c, 0x1e);
                accent = Color.FromRgb(0x3a, 0x9f, 0x5a); header = Color.FromRgb(0x9e, 0xff, 0xb0); text = Color.FromRgb(0xe0, 0xf0, 0xe4);
                sub = Color.FromRgb(0x88, 0xaa, 0x90); border = Color.FromRgb(0x33, 0x55, 0x3d); hover = Color.FromRgb(0x29, 0x4d, 0x33);
                borderStrong = Color.FromRgb(0x7a, 0xff, 0x96);
                break;
            case "紫色":
                bg = Color.FromRgb(0x12, 0x0c, 0x18); panel = Color.FromRgb(0x1a, 0x12, 0x22); item = Color.FromRgb(0x24, 0x18, 0x30);
                accent = Color.FromRgb(0x7a, 0x3f, 0x9f); header = Color.FromRgb(0xd0, 0xa0, 0xff); text = Color.FromRgb(0xf0, 0xe0, 0xf8);
                sub = Color.FromRgb(0xa0, 0x88, 0xaa); border = Color.FromRgb(0x45, 0x33, 0x55); hover = Color.FromRgb(0x3d, 0x29, 0x4d);
                borderStrong = Color.FromRgb(0xc0, 0x8a, 0xff);
                break;
            case "灰色":
                bg = Color.FromRgb(0x15, 0x15, 0x15); panel = Color.FromRgb(0x20, 0x20, 0x20); item = Color.FromRgb(0x2a, 0x2a, 0x2a);
                accent = Color.FromRgb(0x60, 0x70, 0x80); header = Color.FromRgb(0xb0, 0xc0, 0xcc); text = Color.FromRgb(0xe8, 0xe8, 0xe8);
                sub = Color.FromRgb(0x88, 0x88, 0x88); border = Color.FromRgb(0x44, 0x44, 0x44); hover = Color.FromRgb(0x33, 0x33, 0x33);
                borderStrong = Color.FromRgb(0x88, 0x99, 0xaa);
                break;
            case "自定义":
                accent = _customAccent;
                bg = MixColor(Color.FromRgb(0, 0, 0), accent, 0.16f);
                panel = MixColor(bg, accent, 0.20f);
                item = MixColor(panel, accent, 0.12f);
                header = MixColor(Color.FromRgb(255, 255, 255), accent, 0.35f);
                text = MixColor(Color.FromRgb(0xf0, 0xf0, 0xf8), accent, 0.10f);
                sub = MixColor(panel, accent, 0.50f);
                border = MixColor(panel, accent, 0.42f);
                hover = MixColor(panel, accent, 0.28f);
                borderStrong = accent;
                break;
            default:
                bg = Color.FromRgb(0x10, 0x10, 0x18); panel = Color.FromRgb(0x18, 0x18, 0x26); item = Color.FromRgb(0x1e, 0x1e, 0x2c);
                accent = Color.FromRgb(0x3a, 0x5f, 0x9f); header = Color.FromRgb(0x9e, 0xc5, 0xff); text = Color.FromRgb(0xe8, 0xe8, 0xf0);
                sub = Color.FromRgb(0x88, 0x99, 0xaa); border = Color.FromRgb(0x33, 0x44, 0x55); hover = Color.FromRgb(0x29, 0x36, 0x4d);
                borderStrong = Color.FromRgb(0x7a, 0xb8, 0xff);
                break;
        }

        var r = Resources;
        r["ThemeBg"] = new SolidColorBrush(bg);
        r["ThemePanel"] = new SolidColorBrush(panel);
        r["ThemeItem"] = new SolidColorBrush(item);
        r["ThemeAccent"] = new SolidColorBrush(accent);
        r["ThemeHeader"] = new SolidColorBrush(header);
        r["ThemeText"] = new SolidColorBrush(text);
        r["ThemeSubText"] = new SolidColorBrush(sub);
        r["ThemeBorder"] = new SolidColorBrush(border);
        r["ThemeHover"] = new SolidColorBrush(hover);
        r["ThemeBorderStrong"] = new SolidColorBrush(borderStrong);

        if (LibraryFrame != null) LibraryFrame.BorderBrush = new SolidColorBrush(border);
        if (StageFrame != null) StageFrame.BorderBrush = new SolidColorBrush(border);
        if (ModelList != null) { ModelList.Background = new SolidColorBrush(panel); ModelList.Foreground = new SolidColorBrush(text); ModelList.BorderBrush = new SolidColorBrush(border); }
        if (StageList != null) { StageList.Background = new SolidColorBrush(panel); StageList.Foreground = new SolidColorBrush(text); StageList.BorderBrush = new SolidColorBrush(border); }
        if (TimelinePanel != null) TimelinePanel.Background = new SolidColorBrush(panel);
        if (FolderPathText != null) FolderPathText.Foreground = new SolidColorBrush(sub);
    }

    static Color MixColor(Color a, Color b, float t)
    {
        byte Mix(byte x, byte y) => (byte)(x + (y - x) * t);
        return Color.FromArgb(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    void CustomThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var curAccent = (Resources["ThemeAccent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x3a, 0x5f, 0x9f);
        var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(curAccent.R, curAccent.G, curAccent.B)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _customAccent = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            bool hasCustom = false;
            foreach (var obj in ThemeCombo.Items)
            {
                if (obj is ComboBoxItem ci && (string?)ci.Content == "自定义") { hasCustom = true; ThemeCombo.SelectedItem = ci; break; }
            }
            if (!hasCustom)
            {
                var custom = new ComboBoxItem { Content = "自定义" };
                ThemeCombo.Items.Add(custom);
                ThemeCombo.SelectedItem = custom;
            }
            ApplyTheme("自定义");
            SaveTheme("自定义");
            StatusText.Text = "已应用自定义主题色";
        }
    }

    void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText != null) ZoomText.Text = $"{(int)(e.NewValue * 100)}%";
        ApplyZoom((float)e.NewValue);
    }

    void ApplyZoom(float zoom)
    {
        if (_player == null) return;
        try { _player.SetScale(zoom, 0f, 0f); } catch { }
    }

    void ResetZoomBtn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 1.0;

    void Viewport_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_player == null) return;
        double delta = e.Delta / 120.0 * 0.1;
        double val = ZoomSlider.Value + delta;
        if (val < 0.01) val = 0.01;
        if (val > 4.0) val = 4.0;
        ZoomSlider.Value = val;
        e.Handled = true;
    }

    void Viewport_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || _player == null) return;
        _dragging = true;
        _lastDragPoint = e.GetPosition((IInputElement)sender);
        if (sender is UIElement ui) ui.CaptureMouse();
        e.Handled = true;
    }

    void Viewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || _player == null) return;
        var pos = e.GetPosition((IInputElement)sender);
        int dx = (int)(pos.X - _lastDragPoint.X);
        int dy = (int)(pos.Y - _lastDragPoint.Y);
        if (dx != 0 || dy != 0)
        {
            try { _player.OffsetCoord(dx, dy); } catch { }
            _lastDragPoint = pos;
            e.Handled = true;
        }
    }

    void Viewport_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        _dragging = false;
        if (sender is UIElement ui) ui.ReleaseMouseCapture();
        e.Handled = true;
    }

    void Viewport_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _dragging = false;
        if (sender is UIElement ui) ui.ReleaseMouseCapture();
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopRenderingScene();
        if (_emote != null)
        {
            try { _emote.Dispose(); } catch { }
            try { _emote.D3DRelease(); } catch { }
            _emote = null;
        }
        DeleteTempFiles();
    }
}