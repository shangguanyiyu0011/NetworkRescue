using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Documents;

namespace NetworkRescue
{
    public sealed class NativeWindow : Window
    {
        public event Action<string, Dictionary<string, object>> CommandRequested;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 64 };
        private static readonly string[] Languages = { "zh-CN", "zh-TW", "en", "ja", "ko", "de", "fr", "es", "pt", "ru" };
        private static readonly string[] LanguageNames = { "简体中文", "繁體中文", "English", "日本語", "한국어", "Deutsch", "Français", "Español", "Português", "Русский" };
        private static readonly string[,] Checks = { { "Hardware", "hardware" }, { "Connection", "connection" }, { "DhcpService", "dhcp" }, { "Dns", "dns" }, { "Hosts", "hosts" }, { "Proxy", "browser" }, { "Winsock", "winsock" }, { "Http", "internet" } };
        private Brush Ink, Muted, Purple, Line, Back;
        private string theme = "light";
        private Brush PrimaryInk { get { return theme == "dark" ? BrushOf("#151922") : Brushes.White; } }
        private string language = "zh-CN", mode = "beginner", page = "diagnosis", busy, version = "1.0.0", evidenceId = "Hardware";
        private bool cancelled, hasBackup, recovery, initSeen;
        private string historyError, recoveryTime, noticeText;
        private bool noticeError;
        private Dictionary<string, object> report, speed, monitor, site, comparison, updateResult, previousSpeed, advancedResult;
        private readonly List<Dictionary<string, object>> advancedSamples = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> samples = new List<Dictionary<string, object>>(), dnsResults = new List<Dictionary<string, object>>(), siteProbes = new List<Dictionary<string, object>>();
        private object[] history = new object[0], operationDetails = new object[0], adapterRows, connectionRows;
        private readonly HashSet<string> chosen = new HashSet<string>(), beginnerChosen = new HashSet<string>(), currentChosen = new HashSet<string>();
        private sealed class SpeedPoint { public double Seconds; public double? Download, Upload; }
        private readonly Dictionary<string, HashSet<string>> specialChosen = new Dictionary<string, HashSet<string>>();
        private readonly List<SpeedPoint> speedPoints = new List<SpeedPoint>();
        private readonly List<KeyValuePair<Control, Func<bool>>> controls = new List<KeyValuePair<Control, Func<bool>>>();
        private readonly List<Tuple<Button, string, string>> operationButtons = new List<Tuple<Button, string, string>>();
        private StackPanel navigation, body, results;
        private TextBlock titleText, footer, stateText, clockText, versionText;
        private Border notification;
        private ScrollViewer bodyScroll;
        private Button stopButton;
        private Button beginnerModeButton, advancedModeButton;
        private Button settingsShortcut, helpShortcut;
        private Grid headingGrid;
        private TextBox targetInput, hostInput, portInput, intervalInput, countInput, dnsInput, repositoryInput;
        private ComboBox methodInput, routeInput, toolInput;
        private Canvas chart, speedChart;
        private string siteTarget = "", toolHost = "www.microsoft.com", toolPort = "443", monitorHost = "www.microsoft.com", monitorInterval = "1000", monitorCount = "60", dnsHost = "example.com", dnsServers = "223.5.5.5, 1.1.1.1", toolKind = "ping", monitorMethod = "https";
        private bool monitorProxy = true, speedProxy;
        private string advancedOutput = "", adaptersOutput = "", connectionsOutput = "";
        private string inputPage;
        private string updateRepository = UpdateService.DefaultRepository, adapterTimestamp = "", connectionTimestamp = "";
        private sealed class MonitorRow
        {
            public string Id, Host, Method = "https"; public bool Proxy = true;
            public TextBox HostInput; public ComboBox MethodInput, RouteInput;
            public readonly List<Dictionary<string, object>> Samples = new List<Dictionary<string, object>>();
            public Dictionary<string, object> Summary; public StackPanel Result;
        }
        private readonly List<MonitorRow> monitorTargets = new List<MonitorRow>();
        private readonly List<Dictionary<string, object>> monitorEvents = new List<Dictionary<string, object>>();
        private readonly ObservableCollection<Dictionary<string, object>> monitorEventRows = new ObservableCollection<Dictionary<string, object>>();
        private int nextMonitorId = 1, monitorEventTotal; private bool monitorEventTruncated, monitorFailed;
        private string monitorGateway = ""; private Grid monitorGrid; private TextBlock monitorEventCount;
        private object[] healthProfiles = new object[0], monitorPlans = new object[0];
        private bool healthProfilesRequested, monitorPlansRequested, healthCanSave, connectionTruncated;
        private DateTime healthEligibleUntilUtc = DateTime.MaxValue;
        private Dictionary<string, object> healthComparison, healthComparisonProfile, selectedMonitorEvent;
        private string healthProfileName = "", selectedHealthProfile = "", monitorPlanName = "", selectedMonitorPlan = "", connectionSearch = "", connectionFilter = "";
        private TextBox healthProfileNameInput, monitorPlanNameInput, connectionSearchInput;
        private ComboBox healthProfileChoice, monitorPlanChoice, connectionFilterInput;
        private bool monitorPlansExpanded;
        private string pendingMonitorPlanName = "";
        private StackPanel monitorReplayPanel; private DataGrid connectionTable; private TextBlock connectionCountText, connectionEmptyText;
        private int? connectionTotal;
        private string connectionError = "";
        private readonly List<MonitorRow> monitorSessionTargets = new List<MonitorRow>();
        private int monitorSessionIntervalMs = 1000;
        private bool monitorTimelineUpdating;

        public NativeWindow()
        {
            ApplyPalette(); Width = 1280; Height = 860; MinWidth = 960; MinHeight = 650;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Back; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            ConfigureStyles(); BuildShell();
            Loaded += delegate { FitWorkArea(SystemParameters.WorkArea); Request("init", null); };
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += delegate { UpdateClock(); UpdateLocks(); };
            timer.Start(); Closed += delegate { timer.Stop(); };
        }
        private void FitWorkArea(Rect area)
        {
            if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
            double availableWidth = Math.Max(1, area.Width - 24), availableHeight = Math.Max(1, area.Height - 24);
            MinWidth = Math.Min(960, availableWidth); MinHeight = Math.Min(650, availableHeight);
            Width = Math.Min(1280, availableWidth); Height = Math.Min(860, availableHeight);
            WindowStartupLocation = WindowStartupLocation.Manual; Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2;
        }

        public void Receive(string message)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action<string>(Receive), message); return; }
            if (String.IsNullOrEmpty(message) || message.Length > 4 * 1024 * 1024) return;
            Dictionary<string, object> data;
            try { data = json.DeserializeObject(message) as Dictionary<string, object>; } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
            if (data == null) return;
            string type = S(data, "type");
            switch (type)
            {
                case "init":
                    SaveInputs(); string nextTheme = S(data, "theme") == "dark" ? "dark" : "light", nextLanguage = S(data, "language"), nextMode = S(data, "mode") == "advanced" ? "advanced" : "beginner";
                    bool themeOnlySwitch = initSeen && theme != nextTheme && language == nextLanguage && mode == nextMode && (S(data, "version").Length == 0 || version == S(data, "version"));
                    if (theme != nextTheme) { theme = nextTheme; ApplyPalette(); Resources.Clear(); ConfigureStyles(); }
                    if (Languages.Contains(nextLanguage) && language != nextLanguage) { language = nextLanguage; ClearDynamic(); }
                    if (mode != nextMode) { mode = nextMode; chosen.Clear(); currentChosen.Clear(); beginnerChosen.Clear(); specialChosen.Clear(); foreach (Dictionary<string, object> action in Actions(true)) beginnerChosen.Add(ActionKey(action)); page = "diagnosis"; }
                    if (S(data, "version").Length > 0) version = S(data, "version");
                    if (data.ContainsKey("updateRepository")) { if (!themeOnlySwitch) updateRepository = UpdateService.RepositoryOrDefault(S(data, "updateRepository")); repositoryInput = null; }
                    if (data.ContainsKey("monitorGateway")) monitorGateway = S(data, "monitorGateway");
                    if (data.ContainsKey("healthProfiles")) healthProfiles = A(V(data, "healthProfiles"));
                    if (data.ContainsKey("monitorPlans")) monitorPlans = A(V(data, "monitorPlans"));
                    if (data.ContainsKey("canSaveHealthProfile")) healthCanSave = B(data, "canSaveHealthProfile");
                    initSeen = true; BuildShell(); return;
                case "busy": string nextBusy = Empty(S(data, "operation")); if (busy != nextBusy) { cancelled = false; if (nextBusy != null) { ClearNotice(); if (nextBusy == "speed") ResetSpeed(); } } busy = nextBusy; UpdateLocks(); if (page == "diagnosis") RenderPage(); RequestPageProfiles(); return;
                case "progress": footer.Text = S(data, "text"); return;
                case "report":
                    report = D(V(data, "report")); chosen.Clear(); beginnerChosen.Clear(); currentChosen.Clear(); specialChosen.Clear(); if (report != null) ClearNotice();
                    if (data.ContainsKey("canSaveHealthProfile")) healthCanSave = B(data, "canSaveHealthProfile");
                    DateTime reportUtc; if (TryUtc(V(report, "Timestamp"), out reportUtc)) healthEligibleUntilUtc = reportUtc.AddMinutes(5);
                    foreach (Dictionary<string, object> action in Actions(true)) beginnerChosen.Add(ActionKey(action));
                    Dictionary<string, object> focus = Items().FirstOrDefault(delegate(Dictionary<string, object> item) { return A(V(item, "Actions")).Any(delegate(object action) { return B(D(action), "Suggested"); }); }) ?? Items().FirstOrDefault(delegate(Dictionary<string, object> item) { return N(item, "State") == 1 || N(item, "State") == 2; });
                    if (focus != null) evidenceId = S(focus, "Id");
                    if (page == "diagnosis") RenderPage(); UpdateLocks(); return;
                case "speed": speed = D(V(data, "result")); RecordSpeedPoint(speed); if (page == "speed") RenderSpeedResults(); break;
                case "advanced": advancedOutput = S(data, "text"); if (page == "advanced") RenderAdvancedResults(); break;
                case "advancedSample": Dictionary<string, object> advancedSample = D(V(data, "sample")); if (advancedSample != null) { string key = AdvancedSampleKey(advancedSample); int index = advancedSamples.FindIndex(delegate(Dictionary<string, object> row) { return AdvancedSampleKey(row) == key; }); if (index < 0) advancedSamples.Add(advancedSample); else advancedSamples[index] = advancedSample; } if (page == "advanced") RenderAdvancedResults(); break;
                case "advancedResult": advancedResult = D(V(data, "result")); if (advancedResult != null) { advancedSamples.Clear(); advancedSamples.AddRange(A(V(advancedResult, "Rows")).Select(D).Where(delegate(Dictionary<string, object> row) { return row != null; })); } if (page == "advanced") RenderAdvancedResults(); break;
                case "adapters": adaptersOutput = S(data, "text"); adapterRows = data.ContainsKey("Rows") ? A(V(data, "Rows")) : null; adapterTimestamp = DateText(V(data, "Timestamp")); if (page == type) RenderAdapterResults(); break;
                case "connections": connectionsOutput = S(data, "text"); connectionRows = data.ContainsKey("Rows") ? A(V(data, "Rows")) : null; connectionTimestamp = DateText(V(data, "Timestamp")); connectionTotal = data.ContainsKey("TotalRows") ? (int?)N(data, "TotalRows") : data.ContainsKey("TotalCount") ? (int?)N(data, "TotalCount") : null; connectionTruncated = B(data, "Truncated"); connectionError = S(data, "ErrorCode"); if (page == type) RenderConnectionResults(); break;
                case "healthProfiles": SaveInputs(); healthProfiles = A(V(data, "profiles")); healthCanSave = B(data, "canSave"); if (!healthProfiles.Select(D).Any(delegate(Dictionary<string, object> profile) { return S(profile, "Id") == selectedHealthProfile; })) selectedHealthProfile = ""; if (page == "settings" || page == "diagnosis") RenderPage(); break;
                case "healthComparison": healthComparison = D(V(data, "comparison")); healthComparisonProfile = D(V(data, "profile")); if (page == "settings") RenderPage(); break;
                case "monitorPlans": SaveInputs(); monitorPlans = A(V(data, "plans")); if (!monitorPlans.Select(D).Any(delegate(Dictionary<string, object> plan) { return S(plan, "Id") == selectedMonitorPlan; })) selectedMonitorPlan = ""; if (pendingMonitorPlanName.Length > 0) { Dictionary<string, object> saved = monitorPlans.Select(D).FirstOrDefault(delegate(Dictionary<string, object> plan) { return S(plan, "Name") == pendingMonitorPlanName; }); if (saved != null) { selectedMonitorPlan = S(saved, "Id"); Notice(String.Format(U("monitorPlanSaved"), pendingMonitorPlanName), false); } pendingMonitorPlanName = ""; } if (page == "monitor") RenderPage(); break;
                case "healthProfileSaved": Notice(String.Format(U("healthSnapshotSaved"), S(D(V(data, "profile")), "Name")), false); break;
                case "monitorStarted": ApplyMonitorStarted(data); break;
                case "monitorSample": RecordMonitorSample(D(V(data, "sample"))); break;
                case "monitorResult": ApplyMonitorResult(D(V(data, "result"))); break;
                case "networkChange": RecordMonitorEvent(D(V(data, "event"))); break;
                case "monitorEnded":
                    ApplyMonitorEndSnapshots(data);
                    monitorFailed = B(data, "failed"); monitorEventTotal = Math.Max(monitorEventTotal, (int)N(data, "totalEvents")); monitorEventTruncated = monitorEventTruncated || B(data, "truncated");
                    if (busy == "monitor") { busy = null; cancelled = false; } if (page == "monitor") { RefreshMonitorCards(); UpdateMonitorEventCount(); RenderMonitorReplay(); } break;
                case "dnsComparison": ReplaceResult(dnsResults, D(V(data, "result")), "Server"); if (page == "dnscompare") RenderDnsResults(); break;
                case "siteProbe": ReplaceResult(siteProbes, D(V(data, "probe")), "Kind"); if (page == "site") RenderSiteResults(); break;
                case "siteResult": site = D(V(data, "result")); if (page == "site") RenderSiteResults(); break;
                case "history": history = A(V(data, "entries")); historyError = Empty(S(data, "error")); hasBackup = B(data, "hasBackup"); if (page == "history" || page == "diagnosis") RenderPage(); break;
                case "comparison": comparison = D(V(data, "entry")); if (page == "diagnosis" || page == "history") RenderPage(); break;
                case "operationDetails": operationDetails = A(V(data, "details")); if (page == "diagnosis" || page == "history") RenderPage(); break;
                case "recovery": recovery = B(data, "pending"); recoveryTime = DateText(V(data, "StartedUtc")); RenderPage(); break;
                case "updateResult": updateResult = D(V(data, "result")); if (page == "settings") RenderPage(); break;
                case "notice": case "error": if (type == "error") pendingMonitorPlanName = ""; noticeText = S(data, "text"); noticeError = type == "error"; RenderNotice(); break;
            }
            UpdateLocks();
        }

        private void ClearDynamic()
        {
            report = speed = site = comparison = updateResult = advancedResult = null; advancedSamples.Clear(); ResetSpeed(); dnsResults.Clear(); siteProbes.Clear();
            history = operationDetails = new object[0]; adapterRows = null; adapterTimestamp = ""; chosen.Clear(); beginnerChosen.Clear(); currentChosen.Clear(); specialChosen.Clear();
            advancedOutput = adaptersOutput = connectionsOutput = ""; noticeText = null; historyError = null;
        }
        private void ConfigureStyles()
        {
            Style button = new Style(typeof(Button));
            button.Setters.Add(new Setter(Control.BackgroundProperty, Back)); button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            button.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(15, 10, 15, 10))); button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 7)));
            button.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); button.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border)); border.Name = "ButtonFrame"; border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.Name = "ButtonText"; presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty)); presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
            ControlTemplate buttonTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            if (theme == "dark") { Trigger locked = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; locked.Setters.Add(new Setter(Border.BackgroundProperty, BrushOf("#E8E3DE"), "ButtonFrame")); locked.Setters.Add(new Setter(TextElement.ForegroundProperty, Muted, "ButtonText")); buttonTemplate.Triggers.Add(locked); }
            button.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BorderBrushProperty, Purple)); button.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, theme == "dark" ? 1.0 : 0.43)); button.Triggers.Add(disabled);
            Resources.Add(typeof(Button), button);
            Style input = new Style(typeof(TextBox)); input.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 9, 11, 9))); input.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); input.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1))); input.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf("#FFFFFF"))); input.Setters.Add(new Setter(Control.ForegroundProperty, Ink)); input.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            Resources.Add(typeof(TextBox), input);
            Style combo = new Style(typeof(ComboBox)); combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8))); combo.Setters.Add(new Setter(Control.MinHeightProperty, 39.0)); combo.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); combo.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf("#FFFFFF"))); combo.Setters.Add(new Setter(Control.ForegroundProperty, Ink)); Resources.Add(typeof(ComboBox), combo);
            if (theme == "dark") ConfigureDarkStyles();
        }
        private void ConfigureDarkStyles()
        {
            Resources[SystemColors.WindowBrushKey] = BrushOf("#FFFFFF"); Resources[SystemColors.WindowTextBrushKey] = Ink; Resources[SystemColors.ControlBrushKey] = BrushOf("#F1EBE5"); Resources[SystemColors.ControlTextBrushKey] = Ink; Resources[SystemColors.GrayTextBrushKey] = Muted; Resources[SystemColors.HighlightBrushKey] = BrushOf("#344260"); Resources[SystemColors.HighlightTextBrushKey] = Ink; Resources[SystemColors.InactiveSelectionHighlightBrushKey] = BrushOf("#303746"); Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Ink;
            string styles = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Style TargetType='ToolTip'><Setter Property='Background' Value='INSET'/><Setter Property='Foreground' Value='INK'/><Setter Property='BorderBrush' Value='EDGE'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='10'/><Setter Property='MaxWidth' Value='420'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ToolTip'><Border CornerRadius='7' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ComboBoxItem'><Setter Property='Background' Value='INSET'/><Setter Property='Foreground' Value='INK'/><Setter Property='Padding' Value='10,8'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='Cell' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Cell' Property='Background' Value='SELECTED'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Cell' Property='Background' Value='SELECTED'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='MUTED'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ComboBox'><Setter Property='Background' Value='INSET'/><Setter Property='Foreground' Value='INK'/><Setter Property='BorderBrush' Value='EDGE'/><Setter Property='MinHeight' Value='39'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'><Grid>
<ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border x:Name='Frame' Background='INSET' BorderBrush='EDGE' BorderThickness='1' CornerRadius='7'><TextBlock Text='▾' Foreground='MUTED' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='ACCENT'/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton>
<ContentPresenter Margin='11,8,32,8' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' VerticalAlignment='Center' IsHitTestVisible='False'/>
<Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False'><Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' Background='INSET' BorderBrush='EDGE' BorderThickness='1' CornerRadius='7' MaxHeight='320'><ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer></Border></Popup>
</Grid><ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.65'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='ScrollBar'><Setter Property='Background' Value='INSET'/><Setter Property='Width' Value='12'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='{TemplateBinding Background}'><Track x:Name='PART_Track' Orientation='{TemplateBinding Orientation}' IsDirectionReversed='True' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' ViewportSize='{TemplateBinding ViewportSize}' Value='{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
<Track.DecreaseRepeatButton><RepeatButton x:Name='Decrease' Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Grip' Background='EDGE' CornerRadius='5' Margin='2'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Background' Value='ACCENT'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='Grip' Property='Background' Value='ACCENT'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
<Track.IncreaseRepeatButton><RepeatButton x:Name='Increase' Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
</Track></Grid><ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/><Setter TargetName='Decrease' Property='Command' Value='{x:Static ScrollBar.PageLeftCommand}'/><Setter TargetName='Increase' Property='Command' Value='{x:Static ScrollBar.PageRightCommand}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter><Style.Triggers><Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='12'/></Trigger></Style.Triggers></Style>
<Style TargetType='CheckBox'><Setter Property='Foreground' Value='INK'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Border x:Name='Square' Width='16' Height='16' VerticalAlignment='Top' Background='INSET' BorderBrush='EDGE' BorderThickness='1' CornerRadius='3'><TextBlock x:Name='Mark' Text='✓' Visibility='Collapsed' Foreground='ACCENT' FontSize='12' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ContentPresenter Grid.Column='1' Margin='7,0,0,0' RecognizesAccessKey='True'/></Grid><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Mark' Property='Visibility' Value='Visible'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Square' Property='BorderBrush' Value='ACCENT'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.65'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
<Style TargetType='Expander'><Setter Property='Foreground' Value='INK'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Expander'><Grid><Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions><ToggleButton Content='{TemplateBinding Header}' ContentTemplate='{TemplateBinding HeaderTemplate}' HorizontalContentAlignment='Stretch' IsChecked='{Binding IsExpanded,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='Transparent'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><Path x:Name='Arrow' Data='M 0 0 L 4 4 L 8 0' Stroke='MUTED' StrokeThickness='1.6' HorizontalAlignment='Center' VerticalAlignment='Center' RenderTransformOrigin='0.5,0.5'><Path.RenderTransform><RotateTransform Angle='-90'/></Path.RenderTransform></Path><ContentPresenter Grid.Column='1' Margin='4,0,0,0' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/></Grid></Border><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Arrow' Property='RenderTransform'><Setter.Value><RotateTransform Angle='0'/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter x:Name='Body' Grid.Row='1' Margin='18,7,0,0' ContentSource='Content' Visibility='Collapsed'/></Grid><ControlTemplate.Triggers><Trigger Property='IsExpanded' Value='True'><Setter TargetName='Body' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>";
            styles = styles.Replace("INSET", BrushOf("#FFFFFF").ToString()).Replace("INK", Ink.ToString()).Replace("MUTED", Muted.ToString()).Replace("EDGE", Line.ToString()).Replace("ACCENT", Purple.ToString()).Replace("SELECTED", BrushOf("#E3E3F2").ToString());
            ResourceDictionary resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(styles); foreach (object key in resources.Keys) Resources[key] = resources[key];
        }
        private void BuildShell()
        {
            controls.Clear(); Title = T("brand") + " · v" + version;
            Grid shell = new Grid { Background = Back };
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) }); shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border sidebar = new Border { Background = BrushOf("#F1EBE5"), CornerRadius = new CornerRadius(20), Margin = new Thickness(16, 16, 0, 16), Padding = new Thickness(17, 24, 13, 16), BorderBrush = BrushOf("#FFFBF8"), BorderThickness = new Thickness(1) };
            Grid side = new Grid(); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid brand = new Grid { Margin = new Thickness(0, 0, 0, 21) }; brand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); brand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); AutomationProperties.SetAutomationId(brand, "brand-block"); BitmapSource logo = LoadLogo();
            FrameworkElement logoView; if (logo != null) { logoView = new Image { Source = logo, Width = 44, Height = 44, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }; Icon = logo; } else { logoView = Text("◈", 32, Purple); logoView.VerticalAlignment = VerticalAlignment.Center; } AutomationProperties.SetAutomationId(logoView, "brand-logo"); brand.Children.Add(logoView);
            StackPanel brandCopy = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; TextBlock brandName = Text(T("brand"), 16.5, Ink, true), tagline = Text(T("brandTagline"), 10.5, Muted); AutomationProperties.SetAutomationId(brandName, "brand-name"); AutomationProperties.SetAutomationId(tagline, "brand-tagline"); brandCopy.Children.Add(brandName); brandCopy.Children.Add(tagline); Grid.SetColumn(brandCopy, 1); brand.Children.Add(brandCopy); side.Children.Add(brand);
            navigation = new StackPanel(); ScrollViewer navScroll = new ScrollViewer { Content = navigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(navScroll, 1); side.Children.Add(navScroll);
            StackPanel bottom = new StackPanel(); Grid utilityRow = new Grid { Margin = new Thickness(0, 0, 0, 5) }; utilityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); utilityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); utilityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock modeLabel = Text(T(mode), 12, Purple, true); modeLabel.VerticalAlignment = VerticalAlignment.Center; modeLabel.Margin = new Thickness(0, 0, 5, 0); utilityRow.Children.Add(modeLabel);
            settingsShortcut = UtilityButton("settings", "navSettings", "\uE713"); helpShortcut = UtilityButton("help", "navHelp", "\uE897"); Grid.SetColumn(settingsShortcut, 1); Grid.SetColumn(helpShortcut, 2); utilityRow.Children.Add(settingsShortcut); utilityRow.Children.Add(helpShortcut); bottom.Children.Add(utilityRow);
            versionText = Text("v" + version, 12, Muted); bottom.Children.Add(versionText); bottom.Children.Add(Text(T("releaseCandidate"), 10, Muted)); bottom.Margin = new Thickness(10, 15, 0, 0); Grid.SetRow(bottom, 2); side.Children.Add(bottom); sidebar.Child = side; shell.Children.Add(sidebar);
            Grid main = new Grid { Margin = new Thickness(30, 22, 25, 15) }; Grid.SetColumn(main, 1); shell.Children.Add(main);
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 17) }; header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            headingGrid = new Grid(); headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleText = Text("", 25, Ink, true); titleText.MaxWidth = 370; titleText.TextWrapping = TextWrapping.NoWrap; titleText.TextTrimming = TextTrimming.CharacterEllipsis; headingGrid.Children.Add(titleText);
            stateText = Text("", 11, Purple, true); stateText.Margin = new Thickness(12, 9, 0, 0); stateText.VerticalAlignment = VerticalAlignment.Center; stateText.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(stateText, 1); headingGrid.Children.Add(stateText); headingGrid.SizeChanged += delegate { SizeHeading(); }; header.Children.Add(headingGrid);
            clockText = Text("", 11, Muted); clockText.Margin = new Thickness(0, 3, 0, 0); Grid.SetRow(clockText, 1); header.Children.Add(clockText);
            StackPanel modeButtons = new StackPanel { Orientation = Orientation.Horizontal };
            beginnerModeButton = MakeButton("beginner", delegate { if (busy == null) Request("setMode", Values("mode", "beginner")); }, mode == "beginner", false); advancedModeButton = MakeButton("advanced", delegate { if (busy == null) Request("setMode", Values("mode", "advanced")); }, mode == "advanced", false);
            foreach (Button modeButton in new[] { beginnerModeButton, advancedModeButton }) { modeButton.FontSize = 12; modeButton.Padding = new Thickness(12, 9, 12, 9); modeButton.Margin = new Thickness(2); modeButtons.Children.Add(modeButton); }
            Border modeGroup = new Border { Child = modeButtons, CornerRadius = new CornerRadius(13), Padding = new Thickness(3), Margin = new Thickness(16, 0, 0, 0), Background = BrushOf("#ECE5DE"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(modeGroup, 1); Grid.SetRowSpan(modeGroup, 2); header.Children.Add(modeGroup); main.Children.Add(header);
            notification = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 13), Visibility = Visibility.Collapsed }; Grid.SetRow(notification, 1); main.Children.Add(notification);
            body = new StackPanel(); bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(1, 1, 10, 15) }; Grid.SetRow(bodyScroll, 2); main.Children.Add(bodyScroll);
            Grid footerGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) }; footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer = Text(busy == null ? "" : T("working"), 12, Muted); footer.TextTrimming = TextTrimming.CharacterEllipsis; footer.VerticalAlignment = VerticalAlignment.Center; footerGrid.Children.Add(footer);
            stopButton = MakeButton("stop", Stop, false, false); Grid.SetColumn(stopButton, 1); footerGrid.Children.Add(stopButton); Grid.SetRow(footerGrid, 3); main.Children.Add(footerGrid);
            Content = shell; RenderNavigation(); RenderPage(); RenderNotice(); UpdateClock(); UpdateLocks();
        }
        private void RenderNavigation()
        {
            navigation.Children.Clear();
            AddNav("diagnosis", mode == "advanced" ? "workbenchNav" : "navDiagnosis", "◈");
            TextBlock tools = Text(T("toolbox"), 11, Muted); tools.Margin = new Thickness(12, 17, 0, 8); navigation.Children.Add(tools); AddNav("site", "navSite", "◎"); AddNav("speed", "navSpeed", "↗");
            if (mode == "advanced") { AddNav("advanced", "connectivityTools", "⌁"); AddNav("adapters", "navAdapters", "▦"); AddNav("connections", "navConnections", "⇄"); AddNav("monitor", "navMonitor", "∿"); AddNav("dnscompare", "navDnsCompare", "◇"); }
            foreach (Button shortcut in new[] { settingsShortcut, helpShortcut }) { bool selected = page == (shortcut == settingsShortcut ? "settings" : "help"); shortcut.Background = selected ? BrushOf("#E3E3F2") : Back; shortcut.Foreground = selected ? Purple : Muted; }
        }
        private void AddNav(string target, string key, string glyph)
        {
            bool primary = target == "diagnosis", active = page == target;
            Button button = new Button { Content = glyph + "   " + T(key), HorizontalContentAlignment = HorizontalAlignment.Left, Background = primary ? active ? Purple : BrushOf("#E3E3F2") : active ? BrushOf("#E3E3F2") : Back, BorderBrush = primary && active ? Purple : active || primary ? BrushOf("#C4C6E6") : BrushOf("#FFFBF8"), Foreground = primary && active ? PrimaryInk : active || primary ? Purple : Ink, FontWeight = primary ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(0, 3, 1, primary ? 14 : 7), Padding = new Thickness(13, 12, 8, 12), Effect = new DropShadowEffect { Color = theme == "dark" ? Colors.Black : Color.FromRgb(178, 164, 151), Opacity = 0.24, BlurRadius = 9, ShadowDepth = 3 } };
            AutomationProperties.SetAutomationId(button, "nav-" + target);
            button.Click += delegate { Navigate(target); };
            navigation.Children.Add(button);
        }
        private Button UtilityButton(string target, string key, string glyph)
        {
            Button button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }, Width = 36, Height = 36, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), ToolTip = T(key), Focusable = true, IsTabStop = true, Background = Back, Foreground = Muted, BorderBrush = Line };
            AutomationProperties.SetAutomationId(button, "nav-" + target); AutomationProperties.SetName(button, T(key)); button.Click += delegate { Navigate(target); }; return button;
        }
        private void Navigate(string target) { SaveInputs(); ClearNotice(); page = target; RenderNavigation(); RenderPage(); bodyScroll.ScrollToTop(); RequestPageProfiles(); }
        private void RequestPageProfiles() { if (busy != null) return; if (page == "settings" && !healthProfilesRequested) { healthProfilesRequested = true; Request("healthProfiles", null); } if (page == "monitor" && !monitorPlansRequested) { monitorPlansRequested = true; Request("monitorPlans", null); } }
        private void RenderPage()
        {
            if (inputPage == page) SaveInputs();
            controls.Clear(); operationButtons.Clear(); body.Children.Clear(); results = null; chart = null; speedChart = null;
            string key = page == "diagnosis" ? (mode == "advanced" ? "workbenchTitle" : "navDiagnosis") : page == "advanced" ? "connectivityTools" : "nav" + Char.ToUpperInvariant(page[0]) + page.Substring(1);
            if (page == "dnscompare") key = "navDnsCompare";
            titleText.Text = T(key);
            titleText.ToolTip = titleText.Text;
            if (recovery) RenderRecovery();
            switch (page)
            {
                case "diagnosis": RenderDiagnosis(); break;
                case "settings": RenderSettings(); break;
                case "help": RenderHelp(); break;
                case "site": RenderSite(); break;
                case "speed": RenderSpeed(); break;
                case "advanced": RenderAdvanced(); break;
                case "adapters": case "connections": RenderSnapshot(); break;
                case "monitor": RenderMonitor(); break;
                case "dnscompare": RenderDns(); break;
                case "history": RenderHistory(); break;
            }
            inputPage = page;
            UpdateLocks();
        }
        private void RenderRecovery()
        {
            StackPanel card = Card(body, T("recoveryTitle"), T("recoveryHint"), "#F9F5EA"); card.Children.Add(Text(recoveryTime, 12, Muted));
            WrapPanel actions = new WrapPanel(); actions.Children.Add(MakeButton("recoveryScan", delegate { page = "diagnosis"; Start("diagnose", null); }, true)); actions.Children.Add(MakeButton("recoveryRestore", delegate { Start("restoreIncomplete", null); })); actions.Children.Add(MakeButton("recoveryReviewed", delegate { Request("reviewIncomplete", null); })); card.Children.Add(actions);
        }
        private void RenderNotice()
        {
            if (notification == null) return;
            notification.Visibility = String.IsNullOrEmpty(noticeText) ? Visibility.Collapsed : Visibility.Visible;
            notification.Background = BrushOf(noticeError ? "#FBEFEE" : "#ECE9F9");
            notification.Child = Text(noticeError && mode != "advanced" ? T("genericError") : noticeText, 13, noticeError ? BrushOf("#9B4B4B") : Ink);
        }
        private void UpdateClock() { if (clockText != null) clockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture); }
        private void UpdateLocks()
        {
            foreach (KeyValuePair<Control, Func<bool>> pair in controls) pair.Key.IsEnabled = busy == null && (pair.Value == null || pair.Value());
            foreach (Tuple<Button, string, string> item in operationButtons) { bool active = NormalizedOperation(busy) == item.Item2; item.Item1.Content = T(active ? cancelled ? "stopping" : "stop" : item.Item3); AutomationProperties.SetName(item.Item1, Convert.ToString(item.Item1.Content, CultureInfo.InvariantCulture)); item.Item1.IsEnabled = active ? !cancelled : busy == null; }
            if (beginnerModeButton != null) beginnerModeButton.IsEnabled = busy == null;
            if (advancedModeButton != null) advancedModeButton.IsEnabled = busy == null;
            if (stateText != null) stateText.Text = T(busy == null ? "readyFooter" : cancelled ? "stopping" : "working");
            SizeHeading();
            if (stopButton != null) { stopButton.Visibility = CanStop() ? Visibility.Visible : Visibility.Collapsed; stopButton.IsEnabled = !cancelled; }
            if (busy == null && footer != null) footer.Text = "";
        }
        private void SizeHeading() { if (headingGrid == null || headingGrid.ActualWidth <= 0 || stateText == null) return; stateText.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity)); titleText.MaxWidth = Math.Max(85, headingGrid.ActualWidth - stateText.DesiredSize.Width - 18); }
        private static string NormalizedOperation(string value) { return value == "adapters" || value == "connections" ? "advanced" : value; }
        private bool CanStop() { return new[] { "diagnose", "speed", "advanced", "monitor", "dnscompare", "site", "adapters", "connections", "checkUpdates" }.Contains(busy); }
        private void Stop() { if (!CanStop() || cancelled) return; cancelled = true; footer.Text = T("stopping"); UpdateLocks(); Request("cancel", null); }
        private void Request(string command, Dictionary<string, object> values) { if (command != "cancel") ClearNotice(); Action<string, Dictionary<string, object>> handler = CommandRequested; if (handler != null) handler(command, values ?? new Dictionary<string, object>()); }
        private void Start(string command, Dictionary<string, object> values)
        {
            if (busy != null) return; SaveInputs(); noticeText = null; RenderNotice();
            if (command == "diagnose") { report = null; chosen.Clear(); beginnerChosen.Clear(); currentChosen.Clear(); specialChosen.Clear(); }
            if (command == "speed") ResetSpeed();
            busy = command; cancelled = false; footer.Text = T("working"); UpdateLocks(); if (page == "diagnosis") RenderPage(); Request(command, values);
        }

        private void RenderDiagnosis()
        {
            StackPanel banner = Card(body, T(busy == "diagnose" ? "scanTitle" : report == null ? "readyTitle" : B(report, "InternetAvailable") ? "onlineTitle" : "attentionTitle"), T(busy == "diagnose" ? "scanSummary" : report == null ? "readySummary" : "simpleAdvice"), "#E3E3F2");
            WrapPanel top = new WrapPanel(); top.Children.Add(MakeStartButton(report == null ? "startScan" : "scanAgain", "diagnose", delegate { Start("diagnose", null); })); top.Children.Add(MakeButton("exportReport", delegate { Request("export", null); }, false, true, delegate { return report != null; })); if (mode == "advanced") top.Children.Add(MakeButton("restoreConfig", delegate { Start("restore", null); })); banner.Children.Add(top);
            if (B(report, "InternetAvailable")) top.Children.Add(SmallButton("healthProfileSaveEntry", delegate { Request("saveHealthProfile", Values("autoName", true)); }, CanSaveHealthProfile));
            if (report != null) banner.Children.Add(Text(T("lastScan") + "  " + DateText(V(report, "Timestamp")), 12, Muted));
            if (mode == "advanced") { RenderWorkbench(); RenderInsights(); RenderRepairs(); }
            else { RenderBeginnerChecks(); RenderRepairs(); RenderInsights(); }
            RenderComparisons(body);
        }
        private void RenderBeginnerChecks()
        {
            StackPanel list = Card(body, T("eightChecks"), T("diagnosisHint"));
            Grid.SetIsSharedSizeScope(list, true);
            for (int i = 0; i < Checks.GetLength(0); i++)
            {
                Dictionary<string, object> item = Item(Checks[i, 0]);
                Grid row = new Grid { Margin = new Thickness(0, 8, 0, 8) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "BeginnerStatus" });
                StackPanel copy = new StackPanel { Margin = new Thickness(0, 0, 14, 0) }; copy.Children.Add(Text(T(Checks[i, 1]), 14, Ink, true)); copy.Children.Add(Text(PlainSummary(item), 12, Muted)); row.Children.Add(copy); Border status = StatusBadge(item); Grid.SetColumn(status, 1); row.Children.Add(status); list.Children.Add(row);
                if (i < Checks.GetLength(0) - 1) list.Children.Add(new Border { Height = 1, Background = BrushOf("#F0EFF5"), Margin = new Thickness(0, 4, 0, 3) });
            }
        }
        private void RenderWorkbench()
        {
            Grid grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.43, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.57, GridUnitType.Star) }); body.Children.Add(grid);
            StackPanel left = Card(grid, T("eightChecks"), report == null ? T("workbenchEmpty") : S(report, "Summary")); left.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetIsSharedSizeScope(left, true);
            for (int i = 0; i < Checks.GetLength(0); i++)
            {
                string id = Checks[i, 0]; Dictionary<string, object> item = Item(id);
                Grid cells = new Grid(); cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); cells.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "AdvancedStatus" });
                TextBlock name = Text(T(Checks[i, 1]), 13, Ink, true); name.Margin = new Thickness(0, 0, 12, 0); name.VerticalAlignment = VerticalAlignment.Center; cells.Children.Add(name); Border status = StatusBadge(item); Grid.SetColumn(status, 1); cells.Children.Add(status);
                Button row = new Button { Content = cells, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = evidenceId == id ? BrushOf("#E3E3F2") : Back, BorderBrush = evidenceId == id ? BrushOf("#C4C6E6") : Line, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(12, 9, 12, 9) };
                AutomationProperties.SetName(row, T(Checks[i, 1]) + " " + Status(item));
                AutomationProperties.SetAutomationId(row, "check-" + id); row.Click += delegate { if (evidenceId != id) currentChosen.Clear(); evidenceId = id; RenderPage(); }; left.Children.Add(row);
            }
            Dictionary<string, object> selected = Item(evidenceId);
            StackPanel right = Card(grid, selected == null ? T("evidence") : S(selected, "Title"), selected == null ? T("evidencePanelHint") : S(selected, "Summary")); Grid.SetColumn((UIElement)right.Parent, 1);
            RenderEvidence(right, selected);
            RenderCurrentRepairs(right);
        }
        private void RenderEvidence(StackPanel parent, Dictionary<string, object> item, bool includeRaw = true)
        {
            if (item == null) { parent.Children.Add(Output(T("evidenceEmpty"), 200, true)); return; }
            StackPanel groups = new StackPanel();
            foreach (Dictionary<string, object> group in A(V(item, "EvidenceGroups")).Select(D).Where(delegate(Dictionary<string, object> g) { return g != null; }).Take(64))
            {
                StackPanel section = new StackPanel { Margin = new Thickness(0, 5, 0, 12) }; section.Children.Add(Text(S(group, "TitleKey").Length > 0 ? T(S(group, "TitleKey")) : S(group, "Title"), 13, Purple, true)); if (V(group, "State") != null) { Border badge = StatusBadge(group); badge.HorizontalAlignment = HorizontalAlignment.Left; section.Children.Add(badge); }
                foreach (Dictionary<string, object> field in A(V(group, "Fields")).Select(D).Where(delegate(Dictionary<string, object> f) { return f != null; }).Take(64)) section.Children.Add(EvidenceField(S(field, "LabelKey").Length > 0 ? T(S(field, "LabelKey")) : S(field, "Label"), S(field, "Value")));
                if (S(group, "Note").Length > 0) section.Children.Add(Text(S(group, "Note"), 12, Muted));
                groups.Children.Add(section);
            }
            if (groups.Children.Count > 0) { ScrollViewer scroll = new ScrollViewer { Content = groups, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 7, 0, 9) }; AutomationProperties.SetAutomationId(scroll, "evidence-groups"); parent.Children.Add(scroll); }
            string raw = S(item, "Detail"); if (includeRaw && raw.Length > 0) { Expander expander = new Expander { Header = T("rawTechnicalDetails"), Content = Output(raw, 120), IsExpanded = false, Margin = new Thickness(0, 5, 0, 13) }; AutomationProperties.SetAutomationId(expander, "evidence-raw"); parent.Children.Add(expander); }
        }
        private Grid EvidenceField(string label, string value)
        {
            Grid row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.36, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.64, GridUnitType.Star) });
            TextBlock name = Text(label, 12, Muted); name.Margin = new Thickness(0, 2, 10, 4); row.Children.Add(name);
            TextBox text = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Top }; AutomationProperties.SetName(text, label); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
        }
        private Border SmallBadge(string value, string foreground, string background)
        {
            TextBlock text = Text(value, 11.5, BrushOf(foreground), true); text.TextAlignment = TextAlignment.Center; text.Margin = new Thickness(0);
            return new Border { Child = text, Background = BrushOf(background), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 5) };
        }
        private List<Dictionary<string, object>> CurrentActions() { return A(V(Item(evidenceId), "Actions")).Select(D).Where(delegate(Dictionary<string, object> action) { return action != null; }).GroupBy(ActionKey).Select(delegate(IGrouping<string, Dictionary<string, object>> group) { return group.First(); }).ToList(); }
        private void RenderCurrentRepairs(StackPanel parent)
        {
            List<Dictionary<string, object>> actions = CurrentActions();
            parent.Children.Add(Text(T(actions.Count == 0 ? "currentItemNoActions" : "currentItemRepairHint"), 12, Muted));
            foreach (Dictionary<string, object> action in actions)
            {
                string id = ActionKey(action); CheckBox choice = new CheckBox { Content = Text(S(action, "Title"), 13, Ink), Tag = "currentItem", IsChecked = currentChosen.Contains(id), Margin = new Thickness(0, 5, 0, 5) };
                AutomationProperties.SetAutomationId(choice, "current-" + id); choice.Checked += delegate { currentChosen.Add(id); UpdateLocks(); }; choice.Unchecked += delegate { currentChosen.Remove(id); UpdateLocks(); }; Track(choice, null); parent.Children.Add(choice);
                string impactKey = "impact" + S(action, "Id"), impact = T(impactKey); parent.Children.Add(Text(impact == impactKey ? S(action, "Impact") : impact, 12, Muted));
            }
            parent.Children.Add(MakeButton("currentItemRepair", delegate
            {
                object[] selected = CurrentActions().Where(delegate(Dictionary<string, object> a) { return currentChosen.Contains(ActionKey(a)); }).Select(delegate(Dictionary<string, object> a) { return (object)Values("Id", S(a, "Id"), "AdapterId", S(a, "AdapterId")); }).ToArray();
                if (selected.Length > 0) Start("repair", Values("actions", selected));
            }, true, true, delegate { return CurrentActions().Any(delegate(Dictionary<string, object> a) { return currentChosen.Contains(ActionKey(a)); }); }));
        }
        private void RenderRepairs()
        {
            bool beginner = mode != "advanced"; List<Dictionary<string, object>> actions = Actions(beginner); HashSet<string> selection = beginner ? beginnerChosen : chosen;
            string heading = report == null ? "repairBeforeTitle" : actions.Count == 0 ? B(report, "InternetAvailable") && Items().All(delegate(Dictionary<string, object> i) { return N(i, "State") == 0; }) ? "repairHealthyTitle" : "repairManualTitle" : "suggestedRepair";
            StackPanel card = Card(body, T(beginner ? heading : "repairPolicy"), T(report == null ? "repairBeforeHint" : actions.Count == 0 ? "repairManualHint" : "selectedRepairsHint"));
            foreach (Dictionary<string, object> action in actions)
            {
                string id = ActionKey(action); CheckBox check = new CheckBox { IsChecked = selection.Contains(id), Tag = beginner ? "beginnerRepair" : "batchRepair", Margin = new Thickness(0, 9, 0, 9), VerticalContentAlignment = VerticalAlignment.Top };
                AutomationProperties.SetAutomationId(check, (beginner ? "beginner-" : "batch-") + id);
                StackPanel copy = new StackPanel(); copy.Children.Add(Text(S(action, "Title"), 14, Ink, true));
                if (!beginner) copy.Children.Add(Text(S(action, "Description"), 12, Muted));
                string impactKey = "impact" + S(action, "Id"), impact = T(impactKey); if (impact == impactKey) impact = S(action, "Impact"); copy.Children.Add(Text(impact, 12, Muted)); check.Content = copy;
                check.Checked += delegate { selection.Add(id); UpdateLocks(); }; check.Unchecked += delegate { selection.Remove(id); UpdateLocks(); }; Track(check, null); card.Children.Add(check);
            }
            Button repair = MakeButton(beginner ? "oneClickRepair" : "batchRepair", delegate
            {
                object[] selected = Actions(beginner).Where(delegate(Dictionary<string, object> a) { return selection.Contains(ActionKey(a)); }).Select(delegate(Dictionary<string, object> a) { return (object)Values("Id", S(a, "Id"), "AdapterId", S(a, "AdapterId")); }).ToArray();
                if (selected.Length > 0) Start("repair", Values("actions", selected));
            }, true, true, delegate { return report != null && actions.Count > 0 && selection.Count > 0; }); card.Children.Add(repair);
        }
        private void RenderInsights()
        {
            Dictionary<string, object> fault = D(V(report, "Fault"));
            if (fault != null)
            {
                StackPanel card = Card(body, "", ""); AutomationProperties.SetAutomationId(card, "fault-conclusion");
                Grid heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.Children.Add(Text(T("faultLocation"), 18, Ink, true));
                Border confidence = SmallBadge(T("confidence" + S(fault, "Confidence")), S(fault, "Category") == "Healthy" ? "#28694C" : "#3B46B9", S(fault, "Category") == "Healthy" ? "#E2EEE6" : "#E3E3F2"); Grid.SetColumn(confidence, 1); heading.Children.Add(confidence); card.Children.Add(heading);
                card.Children.Add(Text(mode == "advanced" ? S(fault, "Title") : S(fault, "Category") == "Healthy" ? T("simplePassed") : T("simpleUncertain"), 14, Ink, true));
                if (mode == "advanced")
                {
                    object[] metrics = A(V(fault, "Metrics"));
                    if (metrics.Length > 0)
                    {
                        WrapPanel values = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) }; AutomationProperties.SetAutomationId(values, "fault-metrics");
                        foreach (Dictionary<string, object> metric in metrics.Select(D).Where(delegate(Dictionary<string, object> value) { return value != null; }).Take(5))
                        {
                            string kind = S(metric, "Kind"); if (!new[] { "Http", "DirectHttp", "Tcp", "Dns", "Gateway" }.Contains(kind)) continue;
                            StackPanel value = new StackPanel { Width = 160, Margin = new Thickness(0, 4, 16, 8) }; value.Children.Add(Text(U("faultMetric" + kind), 12, Muted));
                            value.Children.Add(Text(N(metric, "Total") > 0 ? N(metric, "Succeeded").ToString("0", CultureInfo.CurrentCulture) + " / " + N(metric, "Total").ToString("0", CultureInfo.CurrentCulture) : U("faultMetricNotTested"), 18, N(metric, "Total") > 0 && N(metric, "Succeeded") == N(metric, "Total") ? BrushOf("#28694C") : Ink, true)); values.Children.Add(value);
                        }
                        card.Children.Add(values); card.Children.Add(Text(U("faultMetricLegend"), 11, Muted));
                    }
                    else foreach (object item in A(V(fault, "Evidence"))) card.Children.Add(Text(Convert.ToString(item, CultureInfo.InvariantCulture), 12, Muted));
                    object[] next = A(V(fault, "NextSteps")); if (next.Length > 0) { card.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 12, 0, 10) }); card.Children.Add(Text(T("nextSteps"), 12, Purple, true)); foreach (object item in next) card.Children.Add(Text(Convert.ToString(item, CultureInfo.InvariantCulture), 13, Ink)); }
                }
                else card.Children.Add(Text(T("simpleAdvice"), 12, Muted));
            }
            object[] specials = A(V(report, "SpecialChecks"));
            if (mode == "advanced" && specials.Length > 0)
            {
                StackPanel card = Card(body, "", ""), content = new StackPanel(); Grid.SetIsSharedSizeScope(content, true);
                Border surface = (Border)card.Parent; surface.Background = BrushOf(theme == "dark" ? "#1B2733" : "#EAF1F6"); surface.BorderBrush = BrushOf(theme == "dark" ? "#3A4A5E" : "#D5E2EC"); AutomationProperties.SetAutomationId(surface, "special-checks-surface");
                Expander group = new Expander { Header = T("specialChecksTitle"), IsExpanded = false, Content = content, FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(group, "special-checks");
                foreach (object value in specials)
                {
                    Dictionary<string, object> special = D(value); Grid row = new Grid { Margin = new Thickness(0, 7, 0, 4) }; row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 230, SharedSizeGroup = "SpecialTitle" }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SpecialStatus" }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SpecialTechnical" }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SpecialRepair" });
                    TextBlock name = Text(S(special, "Title"), 13, Ink, true); name.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(name); Border badge = StatusBadge(special); Grid.SetColumn(badge, 1); row.Children.Add(badge);
                    TextBlock summary = Text(S(special, "Summary"), 12, Muted); summary.MaxHeight = 60; summary.TextTrimming = TextTrimming.CharacterEllipsis; summary.ToolTip = S(special, "Summary"); summary.Margin = new Thickness(12, 3, 12, 0); summary.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(summary, 2); row.Children.Add(summary);
                    string specialId = S(special, "Id"); HashSet<string> selection; if (!specialChosen.TryGetValue(specialId, out selection)) { selection = new HashSet<string>(); specialChosen[specialId] = selection; } HashSet<string> selected = selection;
                    StackPanel detail = new StackPanel { Margin = new Thickness(0, 5, 0, 8) }; RenderEvidence(detail, special, false);
                    foreach (Dictionary<string, object> action in SpecialActions(specialId))
                    {
                        string key = ActionKey(action); CheckBox checkbox = new CheckBox { Content = Text(S(action, "Title"), 12, Ink), IsChecked = selected.Contains(key), Margin = new Thickness(0, 5, 0, 5) }; AutomationProperties.SetAutomationId(checkbox, "special-" + specialId + "-" + key);
                        checkbox.Checked += delegate { selected.Add(key); UpdateLocks(); }; checkbox.Unchecked += delegate { selected.Remove(key); UpdateLocks(); }; Track(checkbox, null); detail.Children.Add(checkbox);
                    }
                    Button repair = MakeButton("currentItemRepair", delegate { object[] actions = SpecialActions(specialId).Where(delegate(Dictionary<string, object> a) { return selected.Contains(ActionKey(a)); }).Select(delegate(Dictionary<string, object> a) { return (object)Values("Id", S(a, "Id"), "AdapterId", S(a, "AdapterId")); }).ToArray(); if (actions.Length > 0) Start("repair", Values("actions", actions)); }, false, true, delegate { return SpecialActions(specialId).Any(delegate(Dictionary<string, object> a) { return selected.Contains(ActionKey(a)); }); });
                    repair.FontSize = 11.5; repair.Content = new TextBlock { Text = T("currentItemRepair"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 100 }; repair.MaxWidth = 124; repair.Padding = new Thickness(9, 6, 9, 6); repair.Margin = new Thickness(12, 0, 0, 0); repair.HorizontalAlignment = HorizontalAlignment.Right; repair.VerticalAlignment = VerticalAlignment.Center; repair.ToolTip = T(N(special, "State") == 0 ? "specialNoRepairNeeded" : SpecialActions(specialId).Count == 0 ? "specialRepairUnavailable" : "specialSelectRepair"); ToolTipService.SetShowOnDisabled(repair, true); AutomationProperties.SetAutomationId(repair, "special-repair-" + specialId); Grid.SetColumn(repair, 4); row.Children.Add(repair);
                    if (detail.Children.Count == 0) detail.Children.Add(Text(T(N(special, "State") == 0 ? "specialNoRepairNeeded" : "specialRepairUnavailable"), 12, Muted));
                    TextBox raw = Output(S(special, "Detail"), 120); raw.Visibility = Visibility.Collapsed; detail.Children.Add(raw);
                    Expander technical = new Expander { Header = Text(T("rawTechnicalDetails"), 11.5, Muted), FontWeight = FontWeights.Normal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = S(special, "Detail").Length > 0 }; ((TextBlock)technical.Header).MaxWidth = 150; AutomationProperties.SetAutomationId(technical, "special-details-" + specialId); Grid.SetColumn(technical, 3); row.Children.Add(technical);
                    Expander entry = new Expander { Header = row, Content = detail, IsExpanded = false, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 7, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetAutomationId(entry, "special-entry-" + specialId);
                    technical.Expanded += delegate { raw.Visibility = Visibility.Visible; entry.IsExpanded = true; }; technical.Collapsed += delegate { raw.Visibility = Visibility.Collapsed; entry.IsExpanded = false; }; entry.Collapsed += delegate { technical.IsExpanded = false; };
                    Action fit = delegate { double width = Math.Max(1, surface.ActualWidth - surface.Padding.Left - surface.Padding.Right - surface.BorderThickness.Left - surface.BorderThickness.Right - 44); bool compact = width < 780; row.Width = width; row.ColumnDefinitions[0].MaxWidth = compact ? 160 : 230; ((TextBlock)technical.Header).MaxWidth = compact ? 100 : 150; Grid.SetRow(summary, compact ? 1 : 0); Grid.SetColumn(summary, compact ? 0 : 2); Grid.SetColumnSpan(summary, compact ? 5 : 1); summary.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(12, 3, 12, 0); }; surface.SizeChanged += delegate { fit(); }; fit(); content.Children.Add(entry);
                }
                card.Children.Add(group);
            }
        }

        private List<Dictionary<string, object>> SpecialActions(string id)
        {
            Dictionary<string, object> special = A(V(report, "SpecialChecks")).Select(D).FirstOrDefault(delegate(Dictionary<string, object> item) { return S(item, "Id") == id && id.Length > 0; });
            if (special == null || N(special, "State") != 1 && N(special, "State") != 2) return new List<Dictionary<string, object>>();
            HashSet<string> allowed = new HashSet<string>(Actions(false).Select(ActionKey));
            return A(V(special, "Actions")).Select(D).Where(delegate(Dictionary<string, object> action) { return action != null && allowed.Contains(ActionKey(action)); }).GroupBy(ActionKey).Select(delegate(IGrouping<string, Dictionary<string, object>> group) { return group.First(); }).ToList();
        }
        private void RenderSettings()
        {
            StackPanel card = Card(body, T("settingsTitle"), T("settingsIntro"));
            ComboBox languages = new ComboBox { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
            for (int i = 0; i < Languages.Length; i++) languages.Items.Add(new ComboBoxItem { Content = LanguageNames[i], Tag = Languages[i] }); languages.SelectedIndex = Array.IndexOf(Languages, language);
            AutomationProperties.SetAutomationId(languages, "language-selector");
            languages.SelectionChanged += delegate { ComboBoxItem choice = languages.SelectedItem as ComboBoxItem; if (choice != null && busy == null) Request("setLanguage", Values("language", choice.Tag)); }; Track(languages, null); card.Children.Add(Field("language", languages)); card.Children.Add(Text(T("languageHint"), 12, Muted));
            WrapPanel modes = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
            foreach (string value in new[] { "beginner", "advanced" }) { string selected = value; Button button = MakeButton(value, delegate { if (busy == null) Request("setMode", Values("mode", selected)); }, mode == value); modes.Children.Add(button); } card.Children.Add(modes); card.Children.Add(Text(T("modeHint"), 12, Muted));
            StackPanel appearance = Card(body, T("appearanceTitle"), T("appearanceHint")); WrapPanel themes = new WrapPanel();
            foreach (string value in new[] { "light", "dark" }) { string selectedTheme = value; Button choice = MakeButton(value == "light" ? "themeDay" : "themeNight", delegate { if (busy == null && theme != selectedTheme) { SaveInputs(); Request("setTheme", Values("theme", selectedTheme)); } }, theme == value); AutomationProperties.SetAutomationId(choice, "theme-" + value); themes.Children.Add(choice); } appearance.Children.Add(themes);
            RenderHealthProfiles();
            StackPanel records = Card(body, T("navHistory"), T("historyIntro")); Button historyEntry = MakeButton("navHistory", delegate { Navigate("history"); }); AutomationProperties.SetAutomationId(historyEntry, "nav-history"); records.Children.Add(historyEntry);
            StackPanel updates = Card(body, T("updateTitle"), T("updateIntro")); updates.Children.Add(Text(T("currentVersion") + " · " + (S(updateResult, "CurrentVersion").Length > 0 ? S(updateResult, "CurrentVersion") : version), 13, Ink, true));
            repositoryInput = Input(updateRepository); updates.Children.Add(Field("updateRepository", repositoryInput)); updates.Children.Add(Text(T("updateRepositoryHint"), 12, Muted));
            WrapPanel updateButtons = new WrapPanel(); updateButtons.Children.Add(MakeStartButton("checkUpdates", "checkUpdates", delegate { SaveInputs(); updateResult = null; Start("checkUpdates", Values("repository", updateRepository)); })); updateButtons.Children.Add(MakeButton("openUpdate", delegate { Request("openUpdate", null); }, false, true, delegate { return S(updateResult, "Status") == "UpdateAvailable" && S(updateResult, "ReleaseUrl").Length > 0; })); updates.Children.Add(updateButtons);
            string updateStatus = S(updateResult, "Status"); if (updateStatus.Length > 0) updates.Children.Add(Text(T("update" + updateStatus), 13, updateStatus == "UpdateAvailable" ? Purple : Muted)); else if (String.IsNullOrWhiteSpace(updateRepository)) updates.Children.Add(Text(T("updateNotConfigured"), 13, Muted));
            if (S(updateResult, "LatestVersion").Length > 0) updates.Children.Add(Text(T("latestVersion") + " · " + S(updateResult, "LatestVersion"), 13, Ink));
            if (mode == "advanced" && S(updateResult, "ErrorCode").Length > 0) updates.Children.Add(Text(S(updateResult, "ErrorCode"), 12, Muted));
            StackPanel feedback = Card(body, T("feedbackTitle"), T("feedbackIntro")); AutomationProperties.SetAutomationId(feedback, "feedback-section");
            WrapPanel author = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) }; TextBlock credit = Text(U("authorLabel"), 12, Muted); credit.Margin = new Thickness(0, 2, 12, 0); author.Children.Add(credit); author.Children.Add(Text("上官一羽", 13, Ink, true)); feedback.Children.Add(author);
            feedback.Children.Add(Text(T("feedbackEmail"), 12, Muted, true)); Grid contact = new Grid { Margin = new Thickness(0, 4, 0, 14) }; contact.ColumnDefinitions.Add(new ColumnDefinition()); contact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox email = Output("shangguanyiyu0011@gmail.com", 42); email.MaxHeight = 42; email.Height = 42; email.FontFamily = FontFamily; email.FontSize = 13; email.Margin = new Thickness(0); AutomationProperties.SetAutomationId(email, "feedback-email"); AutomationProperties.SetName(email, T("feedbackEmail")); contact.Children.Add(email);
            Button copyEmail = SmallButton("copyEmail", delegate { CopyText("shangguanyiyu0011@gmail.com", "emailCopied"); }); copyEmail.Margin = new Thickness(10, 0, 0, 0); copyEmail.Height = 42; Grid.SetColumn(copyEmail, 1); contact.Children.Add(copyEmail); feedback.Children.Add(contact);
            TextBlock hint = Text(T("feedbackHint"), 12, Muted); hint.LineHeight = 21; hint.MaxWidth = 760; hint.HorizontalAlignment = HorizontalAlignment.Left; hint.Margin = new Thickness(0, 0, 0, 9); feedback.Children.Add(hint);
            WrapPanel feedbackButtons = new WrapPanel(); feedbackButtons.Children.Add(SmallButton("exportRedacted", delegate { Request(report != null ? "export" : "exportHistory", null); }, delegate { return report != null || site != null || history.Length > 0; })); feedback.Children.Add(feedbackButtons);
            feedback.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 8, 0, 12) }); feedback.Children.Add(Text(T("freeUseBody"), 12, Muted));
            StackPanel notices = new StackPanel { Margin = new Thickness(25, 9, 0, 4), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (string key in new[] { "openSourcePending", "reportFakeSales", "reportSalesHint" }) { TextBlock text = Text(T(key), 12, key == "reportFakeSales" ? Ink : Muted, key == "reportFakeSales"); text.LineHeight = 21; text.Margin = new Thickness(0, 0, 0, 10); notices.Children.Add(text); }
            Expander info = new Expander { Header = T("freeUseTitle"), Content = notices, IsExpanded = false, Margin = new Thickness(0, 9, 0, 0), FontSize = 13, HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetAutomationId(info, "feedback-notices"); feedback.Children.Add(info);
            Card(body, T("brand") + "  v" + version, T("aboutText")).Children.Add(Text(T("aboutBoundary"), 12, Muted));
        }
        private void RenderHelp()
        {
            Card(body, T("helpTitle"), T("helpIntro"));
            StackPanel usage = Card(body, T("useHelpTitle"), ""); foreach (string topic in new[] { "Beginner", "Advanced", "Snapshots", "Monitoring", "MonitorPlans", "Replay", "Speed", "Report" })
            {
                TextBlock heading = Text(T("useHelp" + topic + "Title"), 14, Ink, true); heading.LineHeight = 22;
                Expander help = new Expander { Header = heading, Content = HelpArticle(T("useHelp" + topic + "Body")), IsExpanded = false, Padding = new Thickness(0, 10, 0, 10), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), FontWeight = FontWeights.Normal, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                usage.SizeChanged += delegate { heading.MaxWidth = Math.Max(1, usage.ActualWidth - 32); }; AutomationProperties.SetAutomationId(help, "help-" + topic); usage.Children.Add(help);
            }
            foreach (string topic in new[] { "Cable", "Vpn", "Dns", "Portal", "Restore" }) Card(body, T("help" + topic + "Title"), T("help" + topic + "Body"));
        }
        private StackPanel HelpArticle(string text)
        {
            StackPanel article = new StackPanel { Margin = new Thickness(27, 7, 0, 6), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left }; AutomationProperties.SetName(article, text);
            string[] lines = (text ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim(); Match step = Regex.Match(line, @"^(\d+)\.\s*(.*)$");
                if (!step.Success) { TextBlock intro = Text(line, 12.5, Muted); intro.LineHeight = 21; intro.Margin = new Thickness(0, 0, 0, 12); article.Children.Add(intro); continue; }
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 12) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                TextBlock number = Text(step.Groups[1].Value, 12, Purple, true); row.Children.Add(number); TextBlock bodyText = Text(step.Groups[2].Value, 13, Ink); bodyText.LineHeight = 23; bodyText.Margin = new Thickness(0); Grid.SetColumn(bodyText, 1); row.Children.Add(bodyText); article.Children.Add(row);
            }
            return article;
        }
        private bool CanSaveHealthProfile() { return healthCanSave && DateTime.UtcNow <= healthEligibleUntilUtc; }
        private Button SmallButton(string key, Action action, Func<bool> enabled = null) { Button button = MakeButton(key, action, false, true, enabled); button.FontSize = 12; button.Padding = new Thickness(11, 7, 11, 7); return button; }
        private bool ValidProfileName(string name) { if (String.IsNullOrWhiteSpace(name) || name.Length > 64) { Notice(U("profileNameError")); return false; } return true; }
        private ComboBox ProfileChoice(object[] profiles, string selected, string id)
        {
            ComboBox choice = new ComboBox { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 8) }; AutomationProperties.SetAutomationId(choice, id);
            foreach (Dictionary<string, object> profile in profiles.Select(D).Where(delegate(Dictionary<string, object> item) { return item != null; })) { ComboBoxItem item = new ComboBoxItem { Content = S(profile, "Name") + " · " + DateText(V(profile, "CreatedUtc")), Tag = S(profile, "Id") }; choice.Items.Add(item); if (S(profile, "Id") == selected) choice.SelectedItem = item; }
            Track(choice, delegate { return profiles.Length > 0; }); return choice;
        }
        private string SelectedProfileId(ComboBox choice) { ComboBoxItem item = choice == null ? null : choice.SelectedItem as ComboBoxItem; return item == null ? "" : Convert.ToString(item.Tag, CultureInfo.InvariantCulture); }
        private void RenderHealthProfiles()
        {
            StackPanel card = Card(body, U("healthProfilesTitle"), U("healthProfilesHint")); healthProfileNameInput = Input(healthProfileName); healthProfileNameInput.MaxLength = 64; AutomationProperties.SetAutomationId(healthProfileNameInput, "health-profile-name"); card.Children.Add(Field("healthProfileName", healthProfileNameInput));
            healthProfileChoice = ProfileChoice(healthProfiles, selectedHealthProfile, "health-profile-choice"); healthProfileChoice.SelectionChanged += delegate { selectedHealthProfile = SelectedProfileId(healthProfileChoice); UpdateLocks(); }; card.Children.Add(Field("healthProfileChoose", healthProfileChoice));
            WrapPanel actions = new WrapPanel(); actions.Children.Add(SmallButton("healthProfileSave", delegate { SaveInputs(); if (CanSaveHealthProfile() && healthProfiles.Length < 20 && ValidProfileName(healthProfileName)) Request("saveHealthProfile", Values("name", healthProfileName)); }, delegate { return CanSaveHealthProfile() && healthProfiles.Length < 20; })); actions.Children.Add(SmallButton("healthProfileCompare", delegate { Request("compareHealthProfile", Values("id", selectedHealthProfile)); }, delegate { return selectedHealthProfile.Length > 0; })); actions.Children.Add(SmallButton("healthProfileDelete", delegate { Request("deleteHealthProfile", Values("id", selectedHealthProfile)); }, delegate { return selectedHealthProfile.Length > 0; })); actions.Children.Add(SmallButton("healthProfileRefresh", delegate { healthProfilesRequested = true; Request("healthProfiles", null); })); card.Children.Add(actions);
            if (healthProfiles.Length == 0) card.Children.Add(Text(U("healthProfilesEmpty"), 12, Muted)); card.Children.Add(Text(U("healthProfileGate"), 12, Muted)); card.Children.Add(Text(U("profileLimitHint"), 11, Muted));
            if (healthComparison == null) return; StackPanel result = Card(body, U("healthComparisonTitle"), S(healthComparisonProfile, "Name") + " · " + DateText(V(healthComparison, "ComparedUtc"))); AutomationProperties.SetAutomationId(result, "health-comparison-result");
            if (!B(healthComparison, "Complete")) result.Children.Add(Text(U("healthComparisonPartial"), 12, Muted));
            object[] changes = A(V(healthComparison, "Changes")); if (B(healthComparison, "Truncated")) result.Children.Add(Text(String.Format(CultureInfo.GetCultureInfo(language), U("healthComparisonTruncated"), changes.Length, Math.Max(changes.Length, (int)N(healthComparison, "TotalChanges"))), 12, Muted));
            if (changes.Length == 0) { if (B(healthComparison, "Complete") && !B(healthComparison, "Truncated")) result.Children.Add(Text(U("healthComparisonNone"), 13, Ink)); return; }
            DataGrid table = ReadOnlyTable("health-comparison-table"); AddTableColumn(table, "Subject", "monitorEventSubject", 1, 150); AddTableColumn(table, "Change", "monitorEventChange", 2, 250); LimitEventTable(table, new[] { "Subject", "Change" }); table.ItemsSource = changes.Select(D).Where(delegate(Dictionary<string, object> change) { return change != null; }).Select(MonitorEventRow).ToArray(); result.Children.Add(table);
        }

        private void RenderSite()
        {
            StackPanel card = Card(body, T("siteTitle"), T("siteIntro")); targetInput = Input(siteTarget); targetInput.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) StartSite(); }; card.Children.Add(Field("siteInput", targetInput)); card.Children.Add(Text(T("siteScope"), 12, Muted));
            WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 13, 0, 0) }; actions.Children.Add(MakeStartButton("siteStart", "site", StartSite)); actions.Children.Add(MakeButton("exportRedacted", delegate { Request("exportHistory", null); }, false, true, delegate { return site != null; })); card.Children.Add(actions); results = new StackPanel(); body.Children.Add(results); RenderSiteResults();
        }
        private void StartSite() { SaveInputs(); if (String.IsNullOrWhiteSpace(siteTarget)) { Notice(T("siteInputError")); return; } site = null; siteProbes.Clear(); RenderSiteResults(); Start("site", Values("target", siteTarget)); }
        private void RenderSiteResults()
        {
            if (results == null) return; results.Children.Clear();
            StackPanel summary = Card(results, T(site == null ? "siteWaiting" : "site" + S(site, "ConclusionCode")), T("siteLimit")); Dictionary<string, object> target = D(V(site, "Target")); if (target != null) summary.Children.Add(Text(T("siteTarget") + " · " + S(target, "Origin"), 12, Muted));
            IEnumerable<Dictionary<string, object>> probes = site == null ? siteProbes : A(V(site, "Probes")).Select(D).Where(delegate(Dictionary<string, object> p) { return p != null; });
            foreach (string kind in new[] { "SystemDns", "TcpV4", "TcpV6", "HttpDirect", "HttpWindowsProxy" })
            {
                Dictionary<string, object> probe = probes.FirstOrDefault(delegate(Dictionary<string, object> p) { return S(p, "Kind") == kind; }); if (probe == null) continue;
                bool http = kind == "HttpDirect" || kind == "HttpWindowsProxy", skipped = (kind == "TcpV4" || kind == "TcpV6") && S(probe, "Status") == "NoAddress";
                int code = (int)N(probe, "HttpStatusCode");
                string status = B(probe, "ResponseReceived") ? "siteResponded" : B(probe, "Connected") ? "siteConnected" : B(probe, "DnsResolved") ? "siteResolved" : S(probe, "Status") == "NoAddress" ? "siteNoAddress" : "siteNoResponse";
                string explanation = skipped && kind == "TcpV6" ? "siteNoIpv6Explanation" : status;
                if (http && B(probe, "ResponseReceived")) explanation = code == 407 ? "siteProxyAuthenticationRequired" : code == 401 || code == 403 ? "siteHttpAccessRestricted" : code >= 500 ? "siteHttpServerErrorReceived" : code >= 400 ? "siteHttpClientErrorReceived" : code >= 300 ? "siteRedirectExplanation" : code >= 200 ? "siteHttpOkExplanation" : "siteHttpResponseReceived";
                else if (http && (S(probe, "ErrorCode") == "HttpTrustFailure" || S(probe, "ErrorCode") == "HttpSecureChannelFailure")) explanation = "siteTlsValidationOrHandshakeFailed";
                StackPanel card = Card(results, T("site" + kind), T(explanation)); AutomationProperties.SetAutomationId(card, "site-probe-" + kind);
                if (http && code > 0) { card.Children.Add(Text(T("siteResponseCode"), 11, Muted)); card.Children.Add(SmallBadge("HTTP " + code.ToString(CultureInfo.InvariantCulture), code >= 200 && code < 300 ? "#28694C" : "#865A18", code >= 200 && code < 300 ? "#E2EEE6" : "#F3E8CF")); }
                if (http) card.Children.Add(Text(T(kind == "HttpDirect" ? "siteDirectExplanation" : "siteProxyExplanation"), 12, Muted));
                if (skipped) card.Children.Add(SmallBadge(T("siteNotMeasured"), "#62616B", "#E8E3DE")); else card.Children.Add(EvidenceField(T("siteTiming"), Number(V(probe, "DurationMs")) + " ms"));
                if (mode == "advanced")
                {
                    StackPanel fields = new StackPanel(); if (S(probe, "Status").Length > 0) fields.Children.Add(EvidenceField(T("statusColumn"), S(probe, "Status")));
                    string[] addresses = A(V(probe, "Addresses")).Select(delegate(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }).ToArray(); if (S(probe, "Address").Length > 0) addresses = addresses.Concat(new[] { S(probe, "Address") }).Distinct().ToArray();
                    if (addresses.Length > 0) fields.Children.Add(EvidenceField(T("probeAddress"), String.Join(Environment.NewLine, addresses))); if (S(probe, "ErrorCode").Length > 0) fields.Children.Add(EvidenceField(T("probeError"), S(probe, "ErrorCode")));
                    if (fields.Children.Count > 0) { Expander details = new Expander { Header = T("rawTechnicalDetails"), Content = fields, IsExpanded = false, Margin = new Thickness(0, 7, 0, 0) }; AutomationProperties.SetAutomationId(details, "site-details-" + kind); card.Children.Add(details); }
                }
            }
        }
        private void RenderSpeed()
        {
            StackPanel card = Card(body, T("speedTitle"), T("speedIntro")); routeInput = Choice(new[] { "direct", "systemProxy" }, speedProxy ? 1 : 0); card.Children.Add(Field("routeMode", routeInput)); card.Children.Add(Text(T("trafficLimit"), 12, Muted)); card.Children.Add(MakeStartButton("startSpeed", "speed", delegate { SaveInputs(); speed = null; Start("speed", Values("useProxy", speedProxy)); RenderSpeedResults(); })); results = new StackPanel(); body.Children.Add(results); RenderSpeedResults();
        }
        private void RenderSpeedResults()
        {
            if (results == null) return; results.Children.Clear(); WrapPanel stats = new WrapPanel(); AddMetric(stats, T("download"), Number(V(speed, "DownloadMbps")) + " Mbps"); AddMetric(stats, T("upload"), Number(V(speed, "UploadMbps")) + " Mbps"); AddMetric(stats, T("latency"), Number(V(speed, "LatencyMs")) + " ms"); results.Children.Add(stats);
            StackPanel chartCard = Card(results, T("speedAverageChartTitle"), T("speedAverageChartHint")); WrapPanel legend = new WrapPanel(); TextBlock downloadLegend = Text("— " + T("download"), 12, Purple, true); downloadLegend.Margin = new Thickness(0, 0, 22, 6); legend.Children.Add(downloadLegend); legend.Children.Add(Text("— " + T("upload"), 12, BrushOf("#168F97"), true)); chartCard.Children.Add(legend); chartCard.Children.Add(Text(T("speedChartMbps"), 11, Muted));
            speedChart = new Canvas { Height = 210, Background = BrushOf("#F8F5F0"), ClipToBounds = true }; speedChart.SizeChanged += delegate { DrawSpeedChart(); }; chartCard.Children.Add(speedChart); chartCard.Children.Add(Text(T("speedPhaseSeconds"), 11, Muted)); DrawSpeedChart();
            StackPanel card = Card(results, T("speedResultTitle"), ""); AutomationProperties.SetAutomationId(card, "speed-result-card");
            bool completed = B(speed, "Completed"), running = busy == "speed" && !cancelled;
            string state = speed == null ? T(running ? "speedStarting" : "notStarted") : S(speed, "State");
            Border badge = SmallBadge(state, completed ? "#28694C" : speed == null || running ? "#62616B" : "#865A18", completed ? "#E2EEE6" : speed == null || running ? "#E8E3DE" : "#F3E8CF"); AutomationProperties.SetAutomationId(badge, "speed-result-status"); card.Children.Add(badge);
            Grid fields = new Grid { Margin = new Thickness(0, 9, 0, 3) }; fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.RowDefinitions.Add(new RowDefinition()); fields.RowDefinitions.Add(new RowDefinition());
            string[] labels = { "speedNode", "routeMode", "elapsed", "speedTraffic" };
            string[] values = { speed == null ? "speed.cloudflare.com" : S(speed, "Node"), speed == null ? T(speedProxy ? "systemProxy" : "direct") : S(speed, "Route"), Number(V(speed, "ElapsedSeconds")) + " s", speed == null ? "—" : Number((N(speed, "DownloadBytes") + N(speed, "UploadBytes")) / 1048576.0) + " MiB" };
            for (int i = 0; i < labels.Length; i++) { StackPanel field = new StackPanel { Margin = new Thickness(0, 4, i % 2 == 0 ? 16 : 0, 7) }; field.Children.Add(Text(T(labels[i]), 11, Muted)); field.Children.Add(Text(values[i], 12, Ink)); Grid.SetRow(field, i / 2); Grid.SetColumn(field, i % 2); fields.Children.Add(field); } card.Children.Add(fields);
            if (speed != null && S(speed, "Detail").Length > 0)
            {
                if (completed) card.Children.Add(new Expander { Header = T("speedExplanation"), Content = Text(S(speed, "Detail"), 12, Muted), IsExpanded = false, Margin = new Thickness(0, 3, 0, 4) });
                else { TextBlock reason = Text(S(speed, "Detail"), 12, running ? Muted : BrushOf("#865A18")); AutomationProperties.SetAutomationId(reason, "speed-result-reason"); card.Children.Add(reason); }
            }
            card.Children.Add(Text(T("speedResultNote"), 11, Muted));
        }
        private void ResetSpeed() { speed = previousSpeed = null; speedPoints.Clear(); }
        private void RecordSpeedPoint(Dictionary<string, object> next)
        {
            if (next == null) return;
            int phase = (int)N(next, "Phase"); double elapsed = N(next, "PhaseElapsedSeconds"), average = N(next, "PhaseAverageMbps");
            if (phase != 1 && phase != 2 || V(next, "PhaseAverageMbps") == null || new[] { elapsed, average }.Any(delegate(double value) { return value < 0 || Double.IsNaN(value) || Double.IsInfinity(value); })) return;
            bool completed = B(next, "PhaseCompleted");
            if (previousSpeed != null && (int)N(previousSpeed, "Phase") == phase)
            {
                double dt = elapsed - N(previousSpeed, "PhaseElapsedSeconds");
                if (dt < 0 || dt < 0.2 && !completed) return;
                if (dt == 0 && speedPoints.Count > 0) speedPoints.RemoveAt(speedPoints.Count - 1);
            }
            speedPoints.Add(new SpeedPoint { Seconds = elapsed, Download = phase == 1 ? (double?)average : null, Upload = phase == 2 ? (double?)average : null });
            if (speedPoints.Count > 300) speedPoints.RemoveAt(0); previousSpeed = next;
        }

        private void DrawSpeedChart()
        {
            if (speedChart == null) return; speedChart.Children.Clear(); double width = Math.Max(220, speedChart.ActualWidth), max = Math.Max(1, speedPoints.SelectMany(delegate(SpeedPoint p) { return new[] { p.Download ?? 0, p.Upload ?? 0 }; }).DefaultIfEmpty(0).Max()) * 1.15, seconds = Math.Max(1, speedPoints.Count == 0 ? 1 : speedPoints.Max(delegate(SpeedPoint p) { return p.Seconds; }));
            for (int i = 0; i < 4; i++)
            {
                double y = 15 + i * 50; speedChart.Children.Add(new System.Windows.Shapes.Line { X1 = 48, X2 = width - 14, Y1 = y, Y2 = y, Stroke = Line }); TextBlock tick = Text((max * (3 - i) / 3).ToString("0.0", CultureInfo.InvariantCulture), 10, Muted); Canvas.SetLeft(tick, 2); Canvas.SetTop(tick, y - 8); speedChart.Children.Add(tick);
            }
            foreach (bool download in new[] { true, false })
            {
                Polyline line = null, hit = null; List<Tuple<Point, string>> hover = null; Brush color = download ? Purple : BrushOf("#168F97"); int node = 0; Canvas owner = speedChart;
                foreach (SpeedPoint point in speedPoints)
                {
                    double? rate = download ? point.Download : point.Upload; if (!rate.HasValue) { line = null; continue; }
                    if (line == null)
                    {
                        hover = new List<Tuple<Point, string>>(); List<Tuple<Point, string>> segment = hover; ToolTip tip = SpeedTooltip("");
                        hit = new Polyline { Stroke = Brushes.Transparent, StrokeThickness = 16, ToolTip = tip }; ToolTipService.SetInitialShowDelay(hit, 100); ToolTipService.SetShowDuration(hit, 30000);
                        hit.MouseMove += delegate(object sender, System.Windows.Input.MouseEventArgs e) { if (segment.Count == 0) return; Point cursor = e.GetPosition(owner); tip.Content = segment.OrderBy(delegate(Tuple<Point, string> item) { return (item.Item1 - cursor).LengthSquared; }).First().Item2; };
                        speedChart.Children.Add(hit); line = new Polyline { Stroke = color, StrokeThickness = 2.4, IsHitTestVisible = false }; if (!download) line.StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 }); speedChart.Children.Add(line);
                    }
                    double x = 48 + point.Seconds / seconds * (width - 62), y = 165 - rate.Value / max * 150; Point plotted = new Point(x, y); line.Points.Add(plotted); hit.Points.Add(plotted);
                    string text = String.Format(U("speedPointTooltip"), T(download ? "download" : "upload"), point.Seconds.ToString("0.00", CultureInfo.CurrentCulture), rate.Value.ToString("0.00", CultureInfo.CurrentCulture)); hover.Add(Tuple.Create(plotted, text));
                    Ellipse dot = new Ellipse { Width = 4, Height = 4, Fill = color, IsHitTestVisible = false }; Canvas.SetLeft(dot, x - 2); Canvas.SetTop(dot, y - 2); speedChart.Children.Add(dot);
                    Ellipse target = new Ellipse { Width = 16, Height = 16, Fill = Brushes.Transparent, ToolTip = SpeedTooltip(text) }; AutomationProperties.SetAutomationId(target, "speed-node-" + (download ? "download-" : "upload-") + node++); AutomationProperties.SetName(target, text); ToolTipService.SetInitialShowDelay(target, 100); ToolTipService.SetShowDuration(target, 30000); Canvas.SetLeft(target, x - 8); Canvas.SetTop(target, y - 8); speedChart.Children.Add(target);
                }
            }
            for (int i = 0; i < 4; i++) { double x = 48 + i * (width - 62) / 3; TextBlock tick = Text((seconds * i / 3).ToString("0.0", CultureInfo.InvariantCulture), 10, Muted); tick.Width = 48; tick.TextAlignment = i == 0 ? TextAlignment.Left : i == 3 ? TextAlignment.Right : TextAlignment.Center; Canvas.SetLeft(tick, x - (i == 0 ? 0 : i == 3 ? 48 : 24)); Canvas.SetTop(tick, 179); AutomationProperties.SetAutomationId(tick, "speed-x-tick-" + i); speedChart.Children.Add(tick); }
            if (speedPoints.Count == 0) { TextBlock empty = Text(T("speedAverageEmpty"), 12, Muted); empty.Width = width - 70; empty.TextAlignment = TextAlignment.Center; Canvas.SetLeft(empty, 54); Canvas.SetTop(empty, 87); speedChart.Children.Add(empty); }
        }
        private ToolTip SpeedTooltip(string text) { return new ToolTip { Content = text, Background = Back, Foreground = Ink, BorderBrush = Line, FontFamily = FontFamily, FontSize = 12, Padding = new Thickness(10), Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint }; }
        private void RenderAdvanced()
        {
            StackPanel card = Card(body, T("advancedTitle"), T("advancedIntro")); toolInput = Choice(new[] { "Ping", "dnsQuery", "traceRoute", "tcpTest" }, Array.IndexOf(new[] { "ping", "dns", "trace", "tcp" }, toolKind)); hostInput = Input(toolHost); portInput = Input(toolPort); card.Children.Add(Field("toolLabel", toolInput)); card.Children.Add(Field("hostLabel", hostInput)); StackPanel portField = Field("portLabel", portInput); portField.Visibility = toolKind == "tcp" ? Visibility.Visible : Visibility.Collapsed; toolInput.SelectionChanged += delegate { portField.Visibility = toolInput.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed; }; card.Children.Add(portField); card.Children.Add(MakeStartButton("runTool", "advanced", delegate { SaveInputs(); int port; if (!Int32.TryParse(toolPort, out port) || port < 1 || port > 65535) { if (toolKind == "tcp") { Notice(T("portError")); return; } port = 443; } advancedOutput = ""; advancedResult = null; advancedSamples.Clear(); RenderAdvancedResults(); Start("advanced", Values("tool", toolKind, "host", toolHost, "port", port)); })); results = new StackPanel(); body.Children.Add(results); RenderAdvancedResults();
        }
        private static string AdvancedSampleKey(Dictionary<string, object> row) { return S(row, "Kind") + ":" + S(row, "Sequence") + ":" + S(row, "Hop") + ":" + S(row, "Address"); }
        private void RenderAdvancedResults()
        {
            if (results == null) return; results.Children.Clear();
            StackPanel card = Card(results, T("toolOutput"), advancedResult == null ? "" : S(advancedResult, "Summary"));
            string kind = advancedResult == null ? advancedSamples.Count > 0 ? S(advancedSamples[0], "Kind") : "" : S(advancedResult, "Tool");
            if (advancedResult != null)
            {
                card.Children.Add(EvidenceField(T("hostLabel"), S(advancedResult, "Host"))); card.Children.Add(EvidenceField(T("probeDuration"), Number(V(advancedResult, "DurationMs")) + " ms"));
                if (B(advancedResult, "Truncated")) card.Children.Add(Text(T("previewLimited"), 12, Muted));
                if (kind == "Ping")
                {
                    double sent = N(advancedResult, "Sent"), received = N(advancedResult, "Received");
                    card.Children.Add(EvidenceField(T("monitorSent"), received.ToString(CultureInfo.InvariantCulture) + " / " + sent.ToString(CultureInfo.InvariantCulture)));
                    card.Children.Add(EvidenceField(T("monitorLoss"), sent > 0 ? Number((sent - received) / sent * 100) + "%" : "—")); card.Children.Add(EvidenceField(T("probeAverageRtt"), Number(V(advancedResult, "AverageRttMs")) + " ms"));
                }
            }
            if (advancedSamples.Count > 0)
            {
                DataGrid table = ReadOnlyTable("advanced-table");
                string[] fields = kind == "Ping" ? new[] { "Sequence", "Address", "Rtt", "DisplayStatus", "ErrorCode" } : kind == "Trace" ? new[] { "Hop", "Address", "Rtt", "DisplayStatus", "ErrorCode" } : kind == "Dns" ? new[] { "RecordType", "Address", "DisplayStatus", "ErrorCode" } : new[] { "Address", "Port", "Duration", "DisplayStatus", "ErrorCode" };
                foreach (string field in fields) AddTableColumn(table, field, field == "Sequence" ? "probeSequence" : field == "Hop" ? "probeHop" : field == "Address" ? "probeAddress" : field == "Rtt" ? "probeDuration" : field == "Port" ? "portLabel" : field == "Duration" ? "probeDuration" : field == "RecordType" ? "probeRecordType" : field == "ErrorCode" ? "probeError" : "statusColumn", field == "Address" ? 1.6 : 1, field == "Address" ? 140 : 78);
                table.ItemsSource = advancedSamples.Select(delegate(Dictionary<string, object> row)
                {
                    Dictionary<string, object> display = new Dictionary<string, object>(row); string status = S(row, "Status"); display["Rtt"] = Number(V(row, "RttMs")) + " ms"; display["Duration"] = Number(V(row, "DurationMs")) + " ms";
                    display["DisplayStatus"] = T(status == "Success" ? "probeSuccess" : status == "TtlExpired" || status == "TimeExceeded" ? "probeHopReply" : status == "TimedOut" ? "probeTimeout" : status == "Error" ? "probeFailed" : B(row, "Responded") ? "siteResponded" : "probeNoResponse"); display["StateBrush"] = status == "Success" ? BrushOf("#28694C") : status == "TtlExpired" || status == "TimeExceeded" ? Purple : status == "TimedOut" ? BrushOf("#865A18") : Muted; return display;
                }).ToArray(); card.Children.Add(table);
            }
            else if (advancedResult == null && advancedOutput.Length == 0) card.Children.Add(Output(T("outputEmpty"), 170, true));
            if (advancedOutput.Length > 0) card.Children.Add(new Expander { Header = T("rawTechnicalDetails"), Content = Output(advancedOutput, 120), IsExpanded = false, Margin = new Thickness(0, 9, 0, 0) });
        }
        private DataGrid ReadOnlyTable(string id)
        {
            DataGrid table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false, RowHeaderWidth = 0, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Line, BorderThickness = new Thickness(0), Foreground = Ink, Background = Back, RowBackground = Back, AlternatingRowBackground = BrushOf("#F8F5F0"), MinHeight = 100, MaxHeight = 440, FontSize = 12, EnableRowVirtualization = true, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader };
            FrameworkElementFactory heading = new FrameworkElementFactory(typeof(TextBlock)); heading.SetBinding(TextBlock.TextProperty, new Binding()); heading.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); Style headers = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)); headers.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, new DataTemplate { VisualTree = heading })); headers.Setters.Add(new Setter(Control.ForegroundProperty, Ink)); headers.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf("#FFFFFF"))); headers.Setters.Add(new Setter(Control.BorderBrushProperty, Line)); headers.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 7, 5, 7))); table.ColumnHeaderStyle = headers; AutomationProperties.SetAutomationId(table, id); return table;
        }
        private void AddTableColumn(DataGrid table, string field, string labelKey, double width, double minimum)
        {
            Style text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5)));
            if (field == "DisplayStatus") text.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("[StateBrush]")));
            table.Columns.Add(new DataGridTextColumn { Header = T(labelKey), Binding = new Binding("[" + field + "]"), ElementStyle = text, Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = minimum });
        }
        private void RenderSnapshot()
        {
            StackPanel card = Card(body, T(page + "Title"), T(page + "Intro")); string command = page; card.Children.Add(MakeStartButton("refresh", "advanced", delegate { Start(command, null); })); results = new StackPanel(); body.Children.Add(results); if (page == "adapters") RenderAdapterResults(); else RenderConnectionResults();
        }
        private void RenderConnectionResults()
        {
            if (results == null) return; if (connectionRows == null) { SetOutput(connectionsOutput); return; }
            results.Children.Clear(); StackPanel card = Card(results, T("connectionsTitle"), connectionTimestamp == "—" ? "" : connectionTimestamp);
            if (connectionRows.Length == 0) { card.Children.Add(Output(connectionError.Length > 0 ? U("connectionReadUnavailable") : T("connectionsEmpty"), 170, true)); return; }
            if (connectionError.Length > 0) card.Children.Add(Text(U("connectionReadUnavailable"), 12, Muted));
            connectionSearchInput = Input(connectionSearch); AutomationProperties.SetAutomationId(connectionSearchInput, "connection-search"); connectionFilterInput = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 3, 0, 8) }; AutomationProperties.SetAutomationId(connectionFilterInput, "connection-filter"); connectionFilterInput.Items.Add(new ComboBoxItem { Content = U("connectionFilterAll"), Tag = "" });
            foreach (string state in connectionRows.Select(D).Select(delegate(Dictionary<string, object> row) { return S(row, "State"); }).Where(delegate(string state) { return state.Length > 0; }).Distinct().OrderBy(delegate(string state) { return state; }, StringComparer.Ordinal)) connectionFilterInput.Items.Add(new ComboBoxItem { Content = state, Tag = state });
            connectionFilterInput.SelectedItem = connectionFilterInput.Items.Cast<ComboBoxItem>().FirstOrDefault(delegate(ComboBoxItem item) { return (string)item.Tag == connectionFilter; }) ?? connectionFilterInput.Items[0]; connectionFilter = SelectedProfileId(connectionFilterInput); Track(connectionFilterInput, null); WrapPanel filters = new WrapPanel(); filters.Children.Add(Field("connectionSearch", connectionSearchInput, 320)); filters.Children.Add(Field("connectionFilter", connectionFilterInput, 230)); card.Children.Add(filters);
            connectionCountText = Text("", 12, Muted); card.Children.Add(connectionCountText); if (connectionTruncated) card.Children.Add(Text(U("connectionTruncated"), 12, Muted)); connectionEmptyText = Text(U("connectionFilteredEmpty"), 13, Muted); card.Children.Add(connectionEmptyText);
            DataGrid table = ReadOnlyTable("connections-table");
            foreach (string key in new[] { "LocalAddress", "LocalPort", "RemoteAddress", "RemotePort" }) AddTableColumn(table, key, "connection" + key, key.EndsWith("Port", StringComparison.Ordinal) ? 0.65 : 1.5, key.EndsWith("Port", StringComparison.Ordinal) ? 70 : 135);
            FrameworkElementFactory status = new FrameworkElementFactory(typeof(TextBlock)); status.SetBinding(TextBlock.TextProperty, new Binding("[State]")); status.SetBinding(TextBlock.ForegroundProperty, new Binding("[StateBrush]")); status.SetValue(TextBlock.FontSizeProperty, 11.5); status.SetValue(TextBlock.PaddingProperty, new Thickness(7, 5, 7, 5));
            table.Columns.Add(new DataGridTemplateColumn { Header = T("connectionState"), CellTemplate = new DataTemplate { VisualTree = status }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 110 });
            AddTableColumn(table, "DisplayProcessId", "connectionProcessId", 0.65, 65); AddTableColumn(table, "DisplayProcessName", "connectionProcessName", 1.2, 140); AddTableColumn(table, "DisplayProcessStatus", "connectionProcessStatus", 1, 125); connectionTable = table; card.Children.Add(table); RefreshConnectionFilter();
            connectionSearchInput.TextChanged += delegate { connectionSearch = connectionSearchInput.Text; RefreshConnectionFilter(); }; connectionFilterInput.SelectionChanged += delegate { connectionFilter = SelectedProfileId(connectionFilterInput); RefreshConnectionFilter(); };
            if (connectionsOutput.Length > 0) card.Children.Add(new Expander { Header = T("rawTechnicalDetails"), Content = Output(connectionsOutput, 100), IsExpanded = false, Margin = new Thickness(0, 10, 0, 0) });
        }
        private void RefreshConnectionFilter()
        {
            if (connectionTable == null || connectionRows == null || page != "connections") return; string search = connectionSearch.Trim();
            Dictionary<string, object>[] rows = connectionRows.Select(D).Where(delegate(Dictionary<string, object> row) { return row != null && (connectionFilter.Length == 0 || S(row, "State") == connectionFilter) && (search.Length == 0 || new[] { "LocalAddress", "LocalPort", "RemoteAddress", "RemotePort", "ProcessId", "ProcessName" }.Any(delegate(string key) { return S(row, key).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0; })); }).Select(delegate(Dictionary<string, object> row)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(row); string state = S(row, "State"), processStatus = S(row, "ProcessStatus"), processName = S(row, "ProcessName"); bool known = processName.Length > 0 && processName != "Unknown" && (processStatus.Length == 0 || processStatus == "PidNameSnapshot" || processStatus == "Known"); copy["StateBrush"] = state == "Established" ? BrushOf("#28694C") : state == "SynSent" || state == "SynReceived" ? BrushOf("#865A18") : Muted; copy["DisplayProcessId"] = V(row, "ProcessId") == null ? "—" : S(row, "ProcessId"); copy["DisplayProcessName"] = known ? processName : U("connectionProcessUnavailable"); copy["DisplayProcessStatus"] = U(processStatus == "AccessDenied" ? "connectionProcessDenied" : processStatus == "Exited" || processStatus == "ProcessExited" ? "connectionProcessExited" : processStatus == "ProcessChanged" ? "connectionProcessChanged" : known ? "connectionProcessKnown" : "connectionProcessUnavailable"); return copy;
            }).ToArray();
            connectionTable.ItemsSource = rows; connectionEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed; connectionCountText.Text = U("connectionVisibleCount") + " · " + rows.Length + " / " + connectionRows.Length + (connectionTotal.HasValue ? " · " + U("connectionTotalCount") + " · " + connectionTotal.Value : "");
        }
        private void RenderAdapterResults()
        {
            if (results == null) return;
            if (adapterRows == null) { SetOutput(adaptersOutput); return; }
            results.Children.Clear(); if (adapterTimestamp.Length > 0) results.Children.Add(Text(adapterTimestamp, 12, Muted));
            foreach (Dictionary<string, object> adapter in adapterRows.Select(D).Where(delegate(Dictionary<string, object> row) { return row != null; }).OrderByDescending(delegate(Dictionary<string, object> row) { return S(row, "Status") == "Up"; }).ThenBy(delegate(Dictionary<string, object> row) { return S(row, "Name"); }, StringComparer.CurrentCulture))
            {
                StackPanel card = Card(results, S(adapter, "Name"), S(adapter, "Description"));
                bool up = S(adapter, "Status") == "Up", unknown = S(adapter, "Status") == "Unknown" || S(adapter, "ReadError").Length > 0;
                TextBlock label = Text(T(up ? "adapterConnected" : unknown ? "adapterUnknown" : "adapterDisconnected"), 11.5, up ? BrushOf("#28694C") : Muted, true); label.TextAlignment = TextAlignment.Center; label.Margin = new Thickness(0);
                card.Children.Add(new Border { Child = label, Background = BrushOf(up ? "#E2EEE6" : "#E8E3DE"), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
                foreach (string key in new[] { "Type", "Dhcp", "Ipv4", "Ipv6", "Gateways", "Dns", "Mac" })
                {
                    Grid row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(Text(T("adapter" + key), 12, Muted, true)); TextBox value = new TextBox { Text = AdapterValue(adapter, key), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Top, FontSize = 13 }; Grid.SetColumn(value, 1); row.Children.Add(value); card.Children.Add(row);
                }
                if (S(adapter, "ReadError").Length > 0) card.Children.Add(Text(T("adapterReadError") + " " + S(adapter, "ReadError"), 12, Muted));
                card.Children.Add(MakeButton("copyAdapter", delegate { CopyText(AdapterText(adapter), "adapterCopied"); }));
                card.Children.Add(new Expander { Header = T("adapterRawDetails"), Content = Output(json.Serialize(adapter), 100), IsExpanded = false, Margin = new Thickness(0, 7, 0, 0) });
            }
            if (adapterRows.Length == 0) { StackPanel empty = Card(results, "", ""); empty.Children.Add(Output(T("unknown"), 130, true)); }
        }
        private string AdapterValue(Dictionary<string, object> adapter, string key)
        {
            if (key == "Dhcp") return T(V(adapter, key) is bool ? B(adapter, key) ? "automatic" : "manual" : "unknown");
            if (key == "Type" || key == "Mac") return S(adapter, key).Length > 0 ? S(adapter, key) : T("unknown");
            object[] values = A(V(adapter, key)); return values.Length == 0 ? T("emptyValue") : String.Join(Environment.NewLine, values.Select(delegate(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture); }));
        }
        private string AdapterText(Dictionary<string, object> adapter)
        {
            StringBuilder text = new StringBuilder(); text.AppendLine(S(adapter, "Name")); text.AppendLine(S(adapter, "Description"));
            foreach (string key in new[] { "Type", "Dhcp", "Ipv4", "Ipv6", "Gateways", "Dns", "Mac" }) text.AppendLine(T("adapter" + key) + ": " + AdapterValue(adapter, key));
            if (S(adapter, "ReadError").Length > 0) text.AppendLine(T("adapterReadError") + " " + S(adapter, "ReadError")); return text.ToString();
        }
        private void SetOutput(string text) { if (results == null) return; results.Children.Clear(); StackPanel card = Card(results, T("toolOutput"), T("realOutput")); bool empty = String.IsNullOrEmpty(text); card.Children.Add(Output(empty ? T("outputEmpty") : text, 360, empty)); }
        private void RenderMonitor()
        {
            EnsureMonitorTargets(); StackPanel card = Card(body, T("monitorTitle"), T("monitorTargetsIntro"));
            foreach (MonitorRow row in monitorTargets)
            {
                StackPanel target = new StackPanel { Margin = new Thickness(0, 4, 0, 8) }; Grid heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                heading.Children.Add(Text(T("monitorTargetNumber") + " " + (monitorTargets.IndexOf(row) + 1), 13, Purple, true));
                Button remove = MakeButton("monitorRemoveTarget", delegate { if (busy != null || monitorTargets.Count <= 1) return; SaveInputs(); monitorTargets.Remove(row); RenderPage(); }, false, true, delegate { return monitorTargets.Count > 1; }); remove.Padding = new Thickness(10, 4, 10, 4); remove.FontSize = 11; AutomationProperties.SetAutomationId(remove, "monitor-remove-" + row.Id); Grid.SetColumn(remove, 1); heading.Children.Add(remove); target.Children.Add(heading);
                row.HostInput = Input(row.Host); row.MethodInput = Choice(new[] { "monitorHttps", "monitorIcmp" }, row.Method == "icmp" ? 1 : 0); row.RouteInput = Choice(new[] { "monitorProxyFollow", "monitorProxyDirect" }, row.Proxy ? 0 : 1);
                AutomationProperties.SetAutomationId(row.HostInput, "monitor-host-" + row.Id); AutomationProperties.SetAutomationId(row.MethodInput, "monitor-method-" + row.Id); AutomationProperties.SetAutomationId(row.RouteInput, "monitor-route-" + row.Id);
                target.Children.Add(Field("hostLabel", row.HostInput)); WrapPanel choices = new WrapPanel(); choices.Children.Add(Field("monitorMethod", row.MethodInput)); StackPanel route = Field("monitorRoute", row.RouteInput); route.Visibility = row.Method == "icmp" ? Visibility.Collapsed : Visibility.Visible; choices.Children.Add(route); row.MethodInput.SelectionChanged += delegate { route.Visibility = row.MethodInput.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible; }; target.Children.Add(choices); card.Children.Add(target);
            }
            MonitorRow first = monitorTargets[0]; hostInput = first.HostInput; methodInput = first.MethodInput; routeInput = first.RouteInput;
            WrapPanel add = new WrapPanel(); Button addTarget = MakeButton("monitorAddTarget", delegate { AddMonitorTarget("", "https"); }, false, true, delegate { return monitorTargets.Count < 4; }); addTarget.FontSize = 12; addTarget.Padding = new Thickness(11, 7, 11, 7); add.Children.Add(addTarget);
            Button gateway = MakeButton("monitorAddGateway", delegate { AddMonitorTarget(monitorGateway, "icmp"); }, false, true, delegate { string host, error; return monitorTargets.Count < 4 && AdvancedTools.TryNormalizeHost(monitorGateway, out host, out error); }); gateway.FontSize = 12; gateway.Padding = new Thickness(11, 7, 11, 7); gateway.ToolTip = monitorGateway.Length == 0 ? T("monitorGatewayUnknown") : monitorGateway; ToolTipService.SetShowOnDisabled(gateway, true); add.Children.Add(gateway); card.Children.Add(add);
            if (monitorGateway.Length == 0) card.Children.Add(Text(T("monitorGatewayUnknown"), 11, Muted));
            intervalInput = Input(monitorInterval); countInput = Input(monitorCount); WrapPanel parameters = new WrapPanel(); parameters.Children.Add(Field("monitorInterval", intervalInput, 180)); parameters.Children.Add(Field("monitorSamples", countInput, 180)); card.Children.Add(parameters); card.Children.Add(Text(T("monitorWebHint"), 12, Muted)); card.Children.Add(Text(T("monitorIcmpHint"), 12, Muted)); card.Children.Add(Text(T("monitorSessionPrivacy"), 12, Muted));
            WrapPanel actions = new WrapPanel(); actions.Children.Add(MakeStartButton("startMonitor", "monitor", StartMonitor)); actions.Children.Add(MakeButton("monitorExport", delegate { Request("exportMonitoring", null); }, false, true, HasMonitorObservations)); card.Children.Add(actions);
            RenderMonitorPlans(card); results = new StackPanel(); body.Children.Add(results); RenderMonitorResults();
        }
        private string NextMonitorTargetId() { string id; do { id = "target-" + nextMonitorId++; } while (monitorTargets.Any(delegate(MonitorRow row) { return row.Id == id; })); return id; }
        private void EnsureMonitorTargets() { if (monitorTargets.Count == 0) monitorTargets.Add(new MonitorRow { Id = NextMonitorTargetId(), Host = monitorHost, Method = monitorMethod, Proxy = monitorProxy }); }
        private void AddMonitorTarget(string host, string method) { if (busy != null || monitorTargets.Count >= 4) return; SaveInputs(); monitorTargets.Add(new MonitorRow { Id = NextMonitorTargetId(), Host = host, Method = method }); RenderPage(); }
        private List<MonitorRow> ActiveMonitorTargets() { return monitorSessionTargets.Count > 0 ? monitorSessionTargets : monitorTargets; }
        private static MonitorRow MonitorTargetCopy(MonitorRow row) { return new MonitorRow { Id = row.Id, Host = row.Host, Method = row.Method, Proxy = row.Proxy }; }
        private void EnsureMonitorSession() { if (monitorSessionTargets.Count == 0) { EnsureMonitorTargets(); monitorSessionTargets.AddRange(monitorTargets.Select(MonitorTargetCopy)); } }
        private bool HasMonitorObservations() { return monitorEvents.Count > 0 || ActiveMonitorTargets().Any(delegate(MonitorRow row) { return row.Samples.Count > 0 || N(row.Summary, "Sent") > 0; }); }
        private void RenderMonitorPlans(StackPanel parent)
        {
            StackPanel content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; content.Children.Add(Text(U("monitorPlansHint"), 12, Muted));
            Expander section = new Expander { Header = U("monitorPlansTitle"), Content = content, IsExpanded = monitorPlansExpanded, FontSize = 13, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 14, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(section, "monitor-plans-section"); section.Expanded += delegate { monitorPlansExpanded = true; }; section.Collapsed += delegate { monitorPlansExpanded = false; }; parent.Children.Add(section);
            monitorPlanChoice = ProfileChoice(monitorPlans, selectedMonitorPlan, "monitor-plan-choice"); monitorPlanChoice.SelectionChanged += delegate { string id = SelectedProfileId(monitorPlanChoice); if (busy == null && id.Length > 0) { selectedMonitorPlan = id; ApplyMonitorPlan(id); UpdateLocks(); } };
            if (monitorPlans.Length > 0) { content.Children.Add(Field("monitorPlanChoose", monitorPlanChoice)); content.Children.Add(Text(U("monitorPlanUseHint"), 12, Muted)); } else content.Children.Add(Text(U("monitorPlansEmpty"), 12, Muted));
            monitorPlanNameInput = Input(monitorPlanName); monitorPlanNameInput.MaxLength = 64; AutomationProperties.SetAutomationId(monitorPlanNameInput, "monitor-plan-name"); content.Children.Add(Field("monitorPlanName", monitorPlanNameInput)); content.Children.Add(Text(U("monitorPlanNameExample"), 11, Muted));
            WrapPanel actions = new WrapPanel(); actions.Children.Add(SmallButton("monitorPlanSave", delegate { SaveInputs(); if (!ValidProfileName(monitorPlanName)) return; int interval, count; object[] targets = MonitorRequestTargets(out interval, out count); if (targets != null && monitorPlans.Length < 20) { pendingMonitorPlanName = monitorPlanName; Request("saveMonitorPlan", Values("name", monitorPlanName, "targets", targets, "intervalMs", interval, "count", count)); } }, delegate { return mode == "advanced" && monitorPlans.Length < 20 && monitorPlanNameInput != null && !String.IsNullOrWhiteSpace(monitorPlanNameInput.Text); }));
            if (monitorPlans.Length > 0) actions.Children.Add(SmallButton("monitorPlanDelete", delegate { Request("deleteMonitorPlan", Values("id", selectedMonitorPlan)); }, delegate { return selectedMonitorPlan.Length > 0; }));
            actions.Children.Add(SmallButton("monitorPlanRefresh", delegate { monitorPlansRequested = true; Request("monitorPlans", null); })); content.Children.Add(actions); content.Children.Add(Text(U("profileLimitHint"), 11, Muted)); monitorPlanNameInput.TextChanged += delegate { UpdateLocks(); };
        }
        private void ApplyMonitorPlan(string id)
        {
            if (busy != null) return; Dictionary<string, object> plan = monitorPlans.Select(D).FirstOrDefault(delegate(Dictionary<string, object> value) { return S(value, "Id") == id; }); if (plan == null) return;
            int interval = (int)N(plan, "IntervalMs"), count = (int)N(plan, "Count"); object[] targets = A(V(plan, "Targets")); List<MonitorRow> rows = new List<MonitorRow>();
            if (interval < 1000 || interval > 10000 || count < 1 || count > 3600 || targets.Length < 1 || targets.Length > 4) { Notice(U("monitorPlanInvalid")); return; }
            foreach (object value in targets) { Dictionary<string, object> target = D(value); string targetId = S(target, "Id"), host, error, method = S(target, "Method").ToLowerInvariant(); if (!Regex.IsMatch(targetId, @"^[A-Za-z0-9_-]{1,32}$") || rows.Any(delegate(MonitorRow row) { return row.Id == targetId; }) || (method != "https" && method != "icmp") || !AdvancedTools.TryNormalizeHost(S(target, "Host"), out host, out error)) { Notice(U("monitorPlanInvalid")); return; } rows.Add(new MonitorRow { Id = targetId, Host = host, Method = method, Proxy = B(target, "UseSystemProxy") }); }
            SaveInputs(); monitorTargets.Clear(); monitorTargets.AddRange(rows); selectedMonitorPlan = id; monitorInterval = interval.ToString(CultureInfo.InvariantCulture); monitorCount = count.ToString(CultureInfo.InvariantCulture); inputPage = null; RenderPage();
        }
        private object[] MonitorRequestTargets(out int interval, out int count)
        {
            count = 0; if (!Int32.TryParse(monitorInterval, out interval) || interval < 1000 || interval > 10000 || !Int32.TryParse(monitorCount, out count) || count < 1 || count > 3600) { Notice(T("monitorTargetsInputError")); return null; }
            List<object> targets = new List<object>(); foreach (MonitorRow row in monitorTargets) { string host, error; if (!AdvancedTools.TryNormalizeHost(row.Host, out host, out error)) { Notice(T("monitorTargetsInputError")); if (row.HostInput != null) row.HostInput.Focus(); return null; } targets.Add(Values("id", row.Id, "host", host, "method", row.Method, "useSystemProxy", row.Proxy)); } return targets.ToArray();
        }
        private void StartMonitor()
        {
            SaveInputs(); int interval, count; object[] targets = MonitorRequestTargets(out interval, out count); if (targets != null) { Start("monitor", Values("targets", targets, "intervalMs", interval, "count", count)); RefreshMonitorCards(); }
        }
        private void RenderMonitorResults()
        {
            if (results == null || page != "monitor") return; results.Children.Clear(); monitorGrid = new Grid(); AutomationProperties.SetAutomationId(monitorGrid, "monitor-results-grid"); results.Children.Add(monitorGrid);
            foreach (MonitorRow row in ActiveMonitorTargets()) { StackPanel holder = new StackPanel(); monitorGrid.Children.Add(holder); row.Result = Card(holder, "", ""); RenderMonitorCard(row); }
            monitorGrid.SizeChanged += delegate { ArrangeMonitorCards(); }; ArrangeMonitorCards();
            StackPanel timeline = Card(results, T("monitorTimelineTitle"), T("monitorTimelineHint")); monitorEventCount = Text("", 12, Muted); AutomationProperties.SetAutomationId(monitorEventCount, "monitor-event-count"); timeline.Children.Add(monitorEventCount);
            monitorTimelineUpdating = true; monitorEventRows.Clear(); foreach (Dictionary<string, object> value in monitorEvents) monitorEventRows.Add(MonitorEventRow(value)); monitorTimelineUpdating = false;
            DataGrid table = ReadOnlyTable("monitor-timeline"); table.Height = 300; table.MinHeight = 150; table.EnableColumnVirtualization = true; table.CanUserSortColumns = false; table.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader; VirtualizingStackPanel.SetIsVirtualizing(table, true); VirtualizingStackPanel.SetVirtualizationMode(table, System.Windows.Controls.VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(table, true);
            AddTableColumn(table, "Time", "monitorEventTime", 1.1, 140); AddTableColumn(table, "Subject", "monitorEventSubject", 1.3, 150); AddTableColumn(table, "Change", "monitorEventChange", 2.5, 250);
            LimitEventTable(table, new[] { "Time", "Subject", "Change" });
            table.SelectionChanged += delegate { if (monitorTimelineUpdating) return; Dictionary<string, object> selected = table.SelectedItem as Dictionary<string, object>; selectedMonitorEvent = selected == null ? null : monitorEvents.FirstOrDefault(delegate(Dictionary<string, object> value) { return N(value, "Sequence") == N(selected, "Sequence"); }); RenderMonitorReplay(); RefreshMonitorCards(); };
            table.ItemsSource = monitorEventRows; timeline.Children.Add(table); UpdateMonitorEventCount(); monitorReplayPanel = Card(results, U("monitorReplayTitle"), U("monitorReplayHint")); AutomationProperties.SetAutomationId(monitorReplayPanel, "monitor-replay"); RenderMonitorReplay();
            if (selectedMonitorEvent != null) table.SelectedItem = monitorEventRows.FirstOrDefault(delegate(Dictionary<string, object> value) { return N(value, "Sequence") == N(selectedMonitorEvent, "Sequence"); });
        }
        private void LimitEventTable(DataGrid table, string[] fields)
        {
            Style eventRow = new Style(typeof(DataGridRow)); eventRow.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 86.0)); table.RowStyle = eventRow;
            for (int i = 0; i < fields.Length; i++) { DataGridTextColumn column = (DataGridTextColumn)table.Columns[i]; Style cell = new Style(typeof(TextBlock), column.ElementStyle); cell.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 60.0)); cell.Setters.Add(new Setter(UIElement.ClipToBoundsProperty, true)); cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); cell.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top)); cell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("[" + fields[i] + "]"))); column.ElementStyle = cell; }
            FrameworkElementFactory fullValue = new FrameworkElementFactory(typeof(TextBox)); fullValue.SetBinding(TextBox.TextProperty, new Binding { Mode = BindingMode.OneWay }); fullValue.SetValue(TextBox.IsReadOnlyProperty, true); fullValue.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap); fullValue.SetValue(TextBox.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto); fullValue.SetValue(FrameworkElement.WidthProperty, 390.0); fullValue.SetValue(FrameworkElement.MaxHeightProperty, 260.0); fullValue.SetValue(Control.BackgroundProperty, Brushes.Transparent); fullValue.SetValue(Control.ForegroundProperty, Ink); fullValue.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            Style tooltip = new Style(typeof(ToolTip), (Style)FindResource(typeof(ToolTip))); tooltip.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, new DataTemplate { VisualTree = fullValue })); table.Resources[typeof(ToolTip)] = tooltip;
        }
        private void ArrangeMonitorCards()
        {
            if (monitorGrid == null) return; int columns = monitorGrid.ActualWidth >= 700 && ActiveMonitorTargets().Count > 1 ? 2 : 1; monitorGrid.ColumnDefinitions.Clear(); monitorGrid.RowDefinitions.Clear(); for (int i = 0; i < columns; i++) monitorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < monitorGrid.Children.Count; i++) { if (i % columns == 0) monitorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); FrameworkElement child = (FrameworkElement)monitorGrid.Children[i]; Grid.SetRow(child, i / columns); Grid.SetColumn(child, i % columns); child.Margin = new Thickness(0, 0, columns == 2 && i % 2 == 0 ? 14 : 0, 0); }
        }
        private void RefreshMonitorCards() { if (page == "monitor") foreach (MonitorRow row in ActiveMonitorTargets()) RenderMonitorCard(row); }
        private void RenderMonitorCard(MonitorRow row)
        {
            if (page != "monitor" || row.Result == null) return; StackPanel card = row.Result; card.Children.Clear(); Dictionary<string, object> value = row.Summary; bool web = value == null || S(value, "Method").Length == 0 ? row.Method == "https" : S(value, "Method").Equals("HTTPS", StringComparison.OrdinalIgnoreCase);
            string host = S(value, "TargetHost"); if (host.Length == 0) host = S(value, "Host"); if (host.Length == 0) host = row.Host;
            card.Children.Add(Text(host, 16, Ink, true)); card.Children.Add(Text(T(web ? "monitorHttps" : "monitorIcmp") + " · " + T(web ? (S(value, "Route").Length == 0 ? row.Proxy : S(value, "Route") == "WindowsProxy") ? "monitorProxyFollow" : "monitorProxyDirect" : "monitorIcmpHint"), 11, Muted));
            card.Children.Add(EvidenceField(T(web ? "monitorWebSent" : "monitorSent"), value == null ? "—" : S(value, "Received") + " / " + S(value, "Sent"))); card.Children.Add(EvidenceField(T(web ? "monitorNoResponse" : "monitorLoss"), Number(V(value, "LossPercent")) + " %")); card.Children.Add(EvidenceField(T(web ? "monitorWebAvg" : "monitorAvg"), Number(V(value, "Avg")) + " ms")); card.Children.Add(EvidenceField(T(web ? "monitorWebJitter" : "monitorJitter"), Number(V(value, "JitterMs")) + " ms"));
            WrapPanel legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) }; AutomationProperties.SetAutomationId(legend, "monitor-legend-" + row.Id); legend.Children.Add(MonitorLegendItem("monitorCurveResponseLegend", false)); legend.Children.Add(MonitorLegendItem("monitorCurveNoResponseLegend", true)); card.Children.Add(legend); card.Children.Add(Text(U("monitorCurveYAxis"), 11, Muted));
            Canvas targetChart = new Canvas { Height = 200, Background = BrushOf("#FAFAFE"), ClipToBounds = true }; AutomationProperties.SetAutomationId(targetChart, "monitor-chart-" + row.Id); targetChart.SizeChanged += delegate { DrawMonitorChart(targetChart, row.Samples); }; card.Children.Add(targetChart); if (row == ActiveMonitorTargets()[0]) chart = targetChart; DrawMonitorChart(targetChart, row.Samples);
            card.Children.Add(Text(U("monitorCurveXAxis"), 11, Muted)); TextBlock meaning = Text(U("monitorCurveMeaning"), 11, Muted); meaning.LineHeight = 19; meaning.Margin = new Thickness(0, 4, 0, 9); card.Children.Add(meaning);
            card.Children.Add(Text(T(busy == "monitor" ? "monitorRunning" : monitorFailed ? "monitorSessionFailed" : value == null ? "notStarted" : "monitorFinished"), 12, Purple, true));
            if (value != null) { string target = S(value, "ResolvedAddress"); if (target.Length > 0) card.Children.Add(EvidenceField(T(web ? "monitorHttpsTarget" : "actualTarget"), target)); card.Children.Add(Text(T("monitorMinMax") + " · " + Number(V(value, "Min")) + " / " + Number(V(value, "Max")) + " ms", 12, Muted)); if (web) { card.Children.Add(Text(T("monitorHttpMeaning"), 11, Muted)); if (V(value, "HttpStatusCode") != null) card.Children.Add(Text(T("monitorLastHttp") + " · HTTP " + S(value, "HttpStatusCode"), 12, Ink, true)); } if (N(value, "Sent") > 0 && N(value, "Received") == 0) card.Children.Add(Text(T(web ? "monitorHttpNoReply" : "monitorIcmpNoReply"), 12, Ink)); if (N(value, "HttpStatusCode") == 407) card.Children.Add(Text(T("monitorProxyAuth"), 12, Ink)); if (N(value, "Sent") == 0 && S(value, "ErrorCode").Length > 0) card.Children.Add(Text(T("monitorUnavailable") + " · " + S(value, "ErrorCode"), 12, Muted)); }
        }
        private StackPanel MonitorLegendItem(string key, bool missing)
        {
            StackPanel item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 4) }; Canvas icon = new Canvas { Width = 22, Height = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            if (missing) { Ellipse dot = new Ellipse { Width = 6, Height = 6, Fill = BrushOf("#C57B80") }; Canvas.SetLeft(dot, 8); Canvas.SetTop(dot, 4); icon.Children.Add(dot); } else icon.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = 22, Y1 = 7, Y2 = 7, Stroke = Purple, StrokeThickness = 2.5 });
            item.Children.Add(icon); item.Children.Add(Text(U(key), 11.5, Muted)); return item;
        }
        private void DrawMonitorChart(Canvas targetChart, List<Dictionary<string, object>> points)
        {
            targetChart.Children.Clear(); double width = Math.Max(200, targetChart.ActualWidth), max = Math.Max(10, points.Where(delegate(Dictionary<string, object> s) { return B(s, "Success"); }).Select(delegate(Dictionary<string, object> s) { return N(s, "RttMs"); }).DefaultIfEmpty(0).Max()) * 1.15;
            DateTime firstUtc, lastUtc; bool timeAxis = points.Count > 1 && TryUtc(V(points[0], "TimeUtc"), out firstUtc) && TryUtc(V(points[points.Count - 1], "TimeUtc"), out lastUtc) && lastUtc > firstUtc;
            firstUtc = DateTime.MinValue; lastUtc = DateTime.MinValue; if (timeAxis) { TryUtc(V(points[0], "TimeUtc"), out firstUtc); TryUtc(V(points[points.Count - 1], "TimeUtc"), out lastUtc); }
            for (int i = 0; i < 4; i++) targetChart.Children.Add(new System.Windows.Shapes.Line { X1 = 12, X2 = width - 12, Y1 = 16 + i * 48, Y2 = 16 + i * 48, Stroke = Line, StrokeThickness = 1 });
            Polyline segment = null;
            for (int i = 0; i < points.Count; i++)
            {
                Dictionary<string, object> sample = points[i]; DateTime sampleUtc; double x = timeAxis && TryUtc(V(sample, "TimeUtc"), out sampleUtc) ? 14 + (sampleUtc - firstUtc).TotalMilliseconds / (lastUtc - firstUtc).TotalMilliseconds * (width - 28) : 14 + i * (width - 28) / Math.Max(1, points.Count - 1), y;
                bool success = B(sample, "Success") && V(sample, "RttMs") != null;
                if (success) { y = 166 - Math.Min(max, Math.Max(0, N(sample, "RttMs"))) / max * 145; if (segment == null) { segment = new Polyline { Stroke = Purple, StrokeThickness = 2.5 }; targetChart.Children.Add(segment); } segment.Points.Add(new Point(x, y)); }
                else { y = 187; segment = null; }
                Ellipse dot = new Ellipse { Width = success ? 4 : 6, Height = success ? 4 : 6, Fill = success ? Purple : BrushOf("#C57B80") }; Canvas.SetLeft(dot, x - dot.Width / 2); Canvas.SetTop(dot, y - dot.Height / 2); targetChart.Children.Add(dot);
            }
            if (points.Count == 0) { TextBlock empty = Text(T("monitorChartEmpty"), 12, Muted); empty.Width = width - 32; empty.TextAlignment = TextAlignment.Center; Canvas.SetLeft(empty, 16); Canvas.SetTop(empty, 88); targetChart.Children.Add(empty); }
            DateTime selectedUtc; if (timeAxis && TryUtc(V(selectedMonitorEvent, "TimeUtc"), out selectedUtc) && selectedUtc >= firstUtc && selectedUtc <= lastUtc)
            {
                double x = 14 + (selectedUtc - firstUtc).TotalMilliseconds / (lastUtc - firstUtc).TotalMilliseconds * (width - 28); System.Windows.Shapes.Line marker = new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = 12, Y2 = 190, Stroke = BrushOf("#B67B24"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }), ToolTip = U("monitorReplaySelectedTime") + " · " + DateText(selectedUtc) }; AutomationProperties.SetAutomationId(marker, "monitor-replay-marker"); targetChart.Children.Add(marker);
            }
        }
        private void RenderMonitorReplay()
        {
            if (page != "monitor" || monitorReplayPanel == null) return; monitorReplayPanel.Children.Clear(); monitorReplayPanel.Children.Add(Text(U("monitorReplayTitle"), 18, Ink, true)); monitorReplayPanel.Children.Add(Text(U("monitorReplayHint"), 12, Muted));
            DateTime selectedUtc; if (selectedMonitorEvent == null || !TryUtc(V(selectedMonitorEvent, "TimeUtc"), out selectedUtc)) { monitorReplayPanel.Children.Add(Text(U("monitorReplayEmpty"), 13, Muted)); return; }
            monitorReplayPanel.Children.Add(EvidenceField(U("monitorReplaySelectedTime"), DateText(selectedUtc))); Dictionary<string, object> description = MonitorEventRow(selectedMonitorEvent); TextBox eventText = Output(S(description, "Subject") + " · " + S(description, "Change"), 42); eventText.MaxHeight = 88; monitorReplayPanel.Children.Add(eventText); monitorReplayPanel.Children.Add(Text(U("monitorReplayWindow"), 11, Muted));
            System.Windows.Controls.Primitives.UniformGrid targets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 1 }; targets.SizeChanged += delegate { targets.Columns = targets.ActualWidth >= 700 && targets.Children.Count > 1 ? 2 : 1; }; monitorReplayPanel.Children.Add(targets);
            foreach (MonitorRow row in ActiveMonitorTargets())
            {
                string host = S(row.Summary, "TargetHost"); if (host.Length == 0) host = S(row.Summary, "Host"); if (host.Length == 0) host = row.Host; StackPanel card = Card(targets, host, T(row.Method == "icmp" ? "monitorIcmp" : "monitorHttps")); ((Border)card.Parent).Margin = new Thickness(0, 5, 10, 8); AutomationProperties.SetAutomationId(card, "monitor-replay-target-" + row.Id);
                Dictionary<string, object> nearest = null; double offset = Double.MaxValue; DateTime nearestUtc = DateTime.MinValue;
                foreach (Dictionary<string, object> sample in row.Samples) { DateTime sampleUtc; if (!TryUtc(V(sample, "TimeUtc"), out sampleUtc)) continue; double distance = Math.Abs((sampleUtc - selectedUtc).TotalMilliseconds); if (distance < offset) { nearest = sample; offset = distance; nearestUtc = sampleUtc; } }
                if (nearest == null || offset > Math.Max(2000, monitorSessionIntervalMs)) { card.Children.Add(Text(U("monitorReplayNoSample"), 12, Muted)); continue; }
                bool responded = B(nearest, "ResponseReceived") || B(nearest, "Success") || V(nearest, "HttpStatusCode") != null; card.Children.Add(EvidenceField(U("monitorReplaySampleTime"), DateText(nearestUtc))); card.Children.Add(EvidenceField(U("monitorReplayOffset"), Number(offset / 1000) + " s")); card.Children.Add(EvidenceField(U("monitorReplayResponse"), U(responded ? "monitorReplayResponded" : "monitorReplayNoResponse") + (V(nearest, "HttpStatusCode") != null ? " · HTTP " + S(nearest, "HttpStatusCode") : "") + (V(nearest, "RttMs") != null ? " · " + Number(V(nearest, "RttMs")) + " ms" : "")));
            }
            monitorReplayPanel.Children.Add(Text(U("monitorReplayConfiguration"), 14, Ink, true)); Dictionary<string, object>[] changes = monitorEvents.Where(delegate(Dictionary<string, object> value) { DateTime eventUtc; return S(value, "Kind") == "SettingChanged" && TryUtc(V(value, "TimeUtc"), out eventUtc) && Math.Abs((eventUtc - selectedUtc).TotalSeconds) <= 10; }).Select(MonitorEventRow).ToArray();
            if (changes.Length == 0) monitorReplayPanel.Children.Add(Text(U("monitorReplayNoConfiguration"), 12, Muted));
            else { DataGrid table = ReadOnlyTable("monitor-replay-settings"); table.MaxHeight = 190; AddTableColumn(table, "Subject", "monitorEventSubject", 1, 150); AddTableColumn(table, "Change", "monitorEventChange", 2, 250); LimitEventTable(table, new[] { "Subject", "Change" }); table.ItemsSource = changes; monitorReplayPanel.Children.Add(table); }
        }
        private MonitorRow FindMonitorTarget(Dictionary<string, object> value)
        {
            EnsureMonitorTargets(); List<MonitorRow> targets = ActiveMonitorTargets(); string id = S(value, "TargetId"); if (id.Length == 0) id = S(value, "Id");
            if (id.Length > 0) return targets.FirstOrDefault(delegate(MonitorRow row) { return row.Id == id; });
            string host = S(value, "TargetHost"); if (host.Length == 0) host = S(value, "Host");
            if (host.Length > 0) { List<MonitorRow> matches = targets.Where(delegate(MonitorRow row) { return row.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && (S(value, "Method").Length == 0 || row.Method.Equals(S(value, "Method"), StringComparison.OrdinalIgnoreCase)); }).ToList(); if (matches.Count == 1) return matches[0]; }
            return targets.Count == 1 || host.Length == 0 ? targets[0] : null;
        }
        private void ApplyMonitorStarted(Dictionary<string, object> data)
        {
            SaveInputs(); EnsureMonitorTargets(); monitorSessionTargets.Clear(); selectedMonitorEvent = null; int interval; if (Int32.TryParse(monitorInterval, out interval)) monitorSessionIntervalMs = interval; object[] targets = A(V(data, "targets")); List<MonitorRow> started = new List<MonitorRow>();
            if (targets.Length > 0 && targets.Length <= 4)
            {
                foreach (object value in targets)
                {
                    Dictionary<string, object> target = D(value); string id = S(target, "Id"), method = S(target, "Method").ToLowerInvariant(), host, error;
                    if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,32}$") || started.Any(delegate(MonitorRow existing) { return existing.Id == id; }) || (method != "https" && method != "icmp") || !AdvancedTools.TryNormalizeHost(S(target, "Host"), out host, out error)) { started.Clear(); break; }
                    started.Add(new MonitorRow { Id = id, Host = host, Method = method, Proxy = B(target, "UseSystemProxy") });
                }
                if (started.Count == targets.Length) { monitorTargets.Clear(); monitorTargets.AddRange(started.Select(MonitorTargetCopy)); monitorSessionTargets.AddRange(started); }
            }
            monitor = null; samples.Clear(); monitorEvents.Clear(); monitorEventRows.Clear(); monitorEventTotal = 0; monitorEventTruncated = false; monitorFailed = false;
            EnsureMonitorSession();
            if (busy == null) { busy = "monitor"; cancelled = false; }
            if (page == "monitor") { inputPage = null; RenderPage(); }
        }
        private void RecordMonitorSample(Dictionary<string, object> sample)
        {
            if (sample == null) return; EnsureMonitorSession(); MonitorRow row = FindMonitorTarget(sample); if (row == null) return;
            row.Summary = sample; row.Samples.Add(sample); if (row.Samples.Count > 300) row.Samples.RemoveAt(0);
            if (row == ActiveMonitorTargets()[0]) { monitor = sample; samples.Add(sample); if (samples.Count > 300) samples.RemoveAt(0); }
            RenderMonitorCard(row); if (selectedMonitorEvent != null) RenderMonitorReplay();
        }
        private void ApplyMonitorResult(Dictionary<string, object> value)
        {
            if (value == null) return; EnsureMonitorSession(); object[] summaries = A(V(value, "Targets")); if (!value.ContainsKey("Targets")) summaries = new object[] { value };
            foreach (object item in summaries) { Dictionary<string, object> summary = D(item); if (summary == null) continue; MonitorRow row = FindMonitorTarget(summary); if (row == null) continue; row.Summary = summary; if (row == ActiveMonitorTargets()[0]) monitor = summary; RenderMonitorCard(row); }
        }
        private void ApplyMonitorEndSnapshots(Dictionary<string, object> data)
        {
            EnsureMonitorSession();
            if (data.ContainsKey("events"))
            {
                Dictionary<string, object>[] events = A(V(data, "events")).Select(D).Where(delegate(Dictionary<string, object> value) { return value != null; }).ToArray(); monitorEvents.Clear(); monitorEvents.AddRange(events.Skip(Math.Max(0, events.Length - 500)));
                monitorTimelineUpdating = true; monitorEventRows.Clear(); if (page == "monitor") foreach (Dictionary<string, object> value in monitorEvents) monitorEventRows.Add(MonitorEventRow(value)); monitorTimelineUpdating = false;
                if (selectedMonitorEvent != null) selectedMonitorEvent = monitorEvents.FirstOrDefault(delegate(Dictionary<string, object> value) { return N(value, "Sequence") == N(selectedMonitorEvent, "Sequence"); });
                monitorEventTotal = Math.Max(monitorEventTotal, monitorEvents.Select(delegate(Dictionary<string, object> value) { return (int)N(value, "Sequence"); }).DefaultIfEmpty(0).Max());
            }
            foreach (object item in A(V(data, "series")))
            {
                Dictionary<string, object> series = D(item); if (series == null) continue; MonitorRow row = FindMonitorTarget(series); if (row == null) continue;
                row.Samples.Clear(); Dictionary<string, object>[] values = A(V(series, "samples")).Select(D).Where(delegate(Dictionary<string, object> value) { return value != null && (S(value, "TargetId").Length == 0 || S(value, "TargetId") == row.Id); }).ToArray(); row.Samples.AddRange(values.Skip(Math.Max(0, values.Length - 300)));
                Dictionary<string, object> last = row.Samples.LastOrDefault(); if (last != null && N(last, "Sent") > N(row.Summary, "Sent")) row.Summary = last;
                if (row == ActiveMonitorTargets()[0]) { samples.Clear(); samples.AddRange(row.Samples); monitor = row.Summary; }
            }
        }
        private void RecordMonitorEvent(Dictionary<string, object> value)
        {
            if (value == null) return; int sequence = (int)N(value, "Sequence"); if (sequence <= 0) return; EnsureMonitorSession();
            monitorEventTotal = Math.Max(monitorEventTotal, sequence); int index = monitorEvents.FindIndex(delegate(Dictionary<string, object> item) { return N(item, "Sequence") >= sequence; });
            if (index >= 0 && N(monitorEvents[index], "Sequence") == sequence) return; if (index < 0) index = monitorEvents.Count;
            if (monitorEvents.Count == 500 && index == 0) return;
            monitorEvents.Insert(index, value); if (page == "monitor") monitorEventRows.Insert(index, MonitorEventRow(value));
            if (monitorEvents.Count > 500) { monitorEvents.RemoveAt(0); if (page == "monitor") monitorEventRows.RemoveAt(0); monitorEventTruncated = true; }
            if (page == "monitor") { UpdateMonitorEventCount(); if (selectedMonitorEvent != null) RenderMonitorReplay(); }
        }
        private Dictionary<string, object> MonitorEventRow(Dictionary<string, object> value)
        {
            string kind = S(value, "Kind"), setting = S(value, "Setting"), subject = S(value, "TargetHost");
            if (subject.Length == 0) subject = S(value, "AdapterName");
            if (setting.Length > 0) subject = (subject.Length > 0 ? subject + " · " : "") + T("monitorSetting" + setting);
            if (subject.Length == 0) subject = T("monitorTitle");
            string change = T("monitorEvent" + kind); if (kind == "SettingChanged") change += "\n" + MonitorEventValue(V(value, "Before")) + " → " + MonitorEventValue(V(value, "After"));
            if (S(value, "ErrorCode").Length > 0) change += " · " + S(value, "ErrorCode");
            return Values("Sequence", N(value, "Sequence"), "Time", DateText(V(value, "TimeUtc")), "Subject", subject, "Change", change);
        }
        private string MonitorEventValue(object value)
        {
            if (value == null) return T("monitorValueUnknown"); string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text.Length == 0) return T("emptyValue");
            if (new[] { "Unknown", "Unavailable", "Invalid", "NotPresent", "LowerLayerDown", "Dormant", "Testing" }.Contains(text)) return T("monitorValueUnknown");
            return new[] { "Enabled", "Disabled", "Up", "Down" }.Contains(text) ? T("monitorValue" + text) : text;
        }
        private void UpdateMonitorEventCount()
        {
            if (monitorEventCount == null || page != "monitor") return; monitorEventCount.Text = T("monitorTimelineCount") + " · " + monitorEventTotal.ToString(CultureInfo.InvariantCulture) + (monitorEvents.Count == 0 ? " · " + T("monitorTimelineEmpty") : "") + (monitorEventTruncated ? " · " + T("monitorTimelineTruncated") : "");
        }
        private void RenderDns()
        {
            StackPanel card = Card(body, T("dnsCompareTitle"), T("dnsCompareIntro")); hostInput = Input(dnsHost); dnsInput = Input(dnsServers); card.Children.Add(Field("dnsDomain", hostInput)); card.Children.Add(Field("dnsServers", dnsInput)); card.Children.Add(Text(T("dnsComparePrivacy"), 12, Muted)); card.Children.Add(MakeStartButton("startDnsCompare", "dnscompare", delegate { SaveInputs(); object[] servers = Regex.Split(dnsServers, @"[,;\s]+").Where(delegate(string s) { return s.Length > 0; }).Cast<object>().ToArray(); if (servers.Length < 1 || servers.Length > 4) { Notice(T("dnsCompareInputError")); return; } dnsResults.Clear(); RenderDnsResults(); Start("dnscompare", Values("host", dnsHost, "servers", servers)); })); results = new StackPanel(); body.Children.Add(results); RenderDnsResults();
        }
        private void RenderDnsResults()
        {
            if (results == null) return; results.Children.Clear();
            Grid grid = new Grid(); AutomationProperties.SetAutomationId(grid, "dns-comparison-grid"); results.Children.Add(grid);
            foreach (Dictionary<string, object> result in dnsResults)
            {
                StackPanel card = Card(grid, S(result, "Server"), ""); card.Children.Add(EvidenceField(T("probeDuration"), Number(V(result, "DurationMs")) + " ms"));
                foreach (string kind in new[] { "A", "AAAA" })
                {
                    Dictionary<string, object> query = A(V(result, "Queries")).Select(D).FirstOrDefault(delegate(Dictionary<string, object> q) { return S(q, "QueryType") == kind; });
                    StackPanel section = new StackPanel { Height = 140, Margin = new Thickness(0, 6, 0, 0) }; Grid heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.Children.Add(Text(kind + (V(query, "Rcode") == null ? "" : " · RCODE " + S(query, "Rcode")), 13, Ink, true));
                    bool answered = S(query, "Status") == "Answered"; Border badge = SmallBadge(T(answered ? "siteResponded" : "unknown"), answered ? "#28694C" : "#62616B", answered ? "#E2EEE6" : "#E8E3DE"); Grid.SetColumn(badge, 1); heading.Children.Add(badge); section.Children.Add(heading);
                    object[] addresses = A(V(query, "Addresses")); TextBox values = Output(addresses.Length > 0 ? String.Join(Environment.NewLine, addresses.Select(delegate(object a) { return Convert.ToString(a, CultureInfo.InvariantCulture); })) : T(answered ? "dnsNoRecords" : "dnsNoAnswer"), 65); values.MaxHeight = 74; section.Children.Add(values);
                    string extra = B(query, "UsedTcp") ? T("dnsTcpFallback") : S(query, "ErrorCode"); if (extra.Length > 0) section.Children.Add(Text(extra, 11, Muted)); card.Children.Add(section);
                }
            }
            Action arrange = delegate
            {
                int columns = grid.ActualWidth >= 700 && grid.Children.Count > 1 ? 2 : 1; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear(); for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < grid.Children.Count; i++) { if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); UIElement child = grid.Children[i]; Grid.SetRow(child, i / columns); Grid.SetColumn(child, i % columns); ((FrameworkElement)child).Margin = new Thickness(0, 0, columns == 2 && i % 2 == 0 ? 14 : 0, 15); }
            };
            grid.SizeChanged += delegate { arrange(); }; arrange();
        }
        private void RenderHistory()
        {
            StackPanel card = Card(body, T("historyTitle"), T("historyIntro")); WrapPanel buttons = new WrapPanel(); buttons.Children.Add(MakeButton("refresh", delegate { Request("history", null); })); buttons.Children.Add(MakeButton("restoreLatest", delegate { Start("restoreLatest", null); }, false, true, delegate { return hasBackup; })); buttons.Children.Add(MakeButton("exportRedacted", delegate { Request("exportHistory", null); }, true, true, delegate { return history.Length > 0 || report != null || site != null; })); card.Children.Add(buttons); card.Children.Add(Text(T("historyShowing"), 12, Muted)); card.Children.Add(Text(T("redactionReview"), 12, Muted));
            if (historyError != null) Card(body, T("historyReadError"), "");
            if (history.Length == 0) { StackPanel empty = Card(body, "", ""); TextBlock text = Text(T("historyEmpty"), 13, Muted); text.TextAlignment = TextAlignment.Center; text.Padding = new Thickness(10, 40, 10, 40); empty.Children.Add(text); }
            foreach (object value in history.Take(20)) RenderComparison(body, D(value));
            RenderComparisons(body);
        }
        private void RenderComparisons(Panel parent)
        {
            if (comparison != null) RenderComparison(parent, comparison);
            if (mode == "advanced" && operationDetails.Length > 0) { StackPanel card = Card(parent, T("native.resultTitle"), ""); foreach (object detail in operationDetails) card.Children.Add(Text(Convert.ToString(detail, CultureInfo.InvariantCulture), 12, Ink)); }
        }
        private void RenderComparison(Panel parent, Dictionary<string, object> entry)
        {
            if (entry == null) return;
            string outcome = S(entry, "Outcome"), label = T("outcome" + outcome); if (label == "outcome" + outcome) label = T(B(entry, "Success") ? "operationSucceeded" : "operationFailed");
            StackPanel card = Card(parent, T(S(entry, "Operation") == "restore" ? "restoreComparison" : "repairComparison"), label + " · " + DateText(V(entry, "EndedUtc")));
            card.Children.Add(Text(T("beforeRepair") + ": " + Online(V(entry, "BeforeOnline")) + "   →   " + T("afterRepair") + ": " + Online(V(entry, "AfterOnline")), 12, Muted));
            foreach (object action in A(V(entry, "ActionTitles"))) card.Children.Add(Text(Convert.ToString(action, CultureInfo.InvariantCulture), 13, Ink, true));
            foreach (object stepValue in A(V(entry, "Steps"))) { Dictionary<string, object> step = D(stepValue); if (step != null) { string action = T("action" + S(step, "ActionId")); card.Children.Add(Text(action + " · " + T("step" + S(step, "State")), 13, Ink)); } }
            if (B(entry, "RestartRequired")) card.Children.Add(Text(T("native.restart"), 12, Purple));
            object[] changes = A(V(entry, "ConfigurationChanges")); if (changes.Length == 0) card.Children.Add(Text(T("noConfirmedChanges"), 12, Muted));
            else foreach (object value in changes) { Dictionary<string, object> change = D(value); string key = S(change, "Key"); card.Children.Add(Text(SettingLabel(key) + "  " + ConfigText(S(change, "Before")) + " → " + ConfigText(S(change, "After")), 12, Muted)); }
            if (B(entry, "PreviewLimited")) card.Children.Add(Text(T("previewLimited"), 12, Muted));
        }

        private void SaveInputs()
        {
            if (page == "site" && targetInput != null) siteTarget = targetInput.Text.Trim();
            if (page == "speed" && routeInput != null) speedProxy = routeInput.SelectedIndex == 1;
            if (page == "advanced" && hostInput != null) { toolHost = hostInput.Text.Trim(); toolPort = portInput.Text.Trim(); toolKind = new[] { "ping", "dns", "trace", "tcp" }[Math.Max(0, toolInput.SelectedIndex)]; }
            if (page == "monitor" && intervalInput != null) { foreach (MonitorRow row in monitorTargets) { if (row.HostInput == null) continue; row.Host = row.HostInput.Text.Trim(); row.Method = row.MethodInput.SelectedIndex == 1 ? "icmp" : "https"; row.Proxy = row.RouteInput.SelectedIndex == 0; } monitorInterval = intervalInput.Text.Trim(); monitorCount = countInput.Text.Trim(); if (monitorTargets.Count > 0) { monitorHost = monitorTargets[0].Host; monitorMethod = monitorTargets[0].Method; monitorProxy = monitorTargets[0].Proxy; } }
            if (page == "monitor" && monitorPlanNameInput != null) monitorPlanName = monitorPlanNameInput.Text.Trim();
            if (page == "dnscompare" && hostInput != null) { dnsHost = hostInput.Text.Trim(); dnsServers = dnsInput.Text.Trim(); }
            if (page == "settings" && repositoryInput != null) { updateRepository = UpdateService.RepositoryOrDefault(repositoryInput.Text); repositoryInput.Text = updateRepository; }
            if (page == "settings" && healthProfileNameInput != null) healthProfileName = healthProfileNameInput.Text.Trim();
        }
        private void ClearNotice() { noticeText = null; noticeError = false; RenderNotice(); }
        private void Notice(string value, bool error = true) { noticeText = value; noticeError = error; RenderNotice(); }
        private void CopyText(string value, string successKey)
        {
            try { Clipboard.SetText(value ?? ""); Notice(T(successKey), false); }
            catch (System.Runtime.InteropServices.ExternalException) { Notice(T("genericError")); }
            catch (System.Security.SecurityException) { Notice(T("genericError")); }
        }
        private List<Dictionary<string, object>> Items() { return A(V(report, "Items")).Select(D).Where(delegate(Dictionary<string, object> d) { return d != null; }).ToList(); }
        private Dictionary<string, object> Item(string id) { return Items().FirstOrDefault(delegate(Dictionary<string, object> item) { return S(item, "Id") == id; }); }
        private List<Dictionary<string, object>> Actions(bool suggested)
        {
            return Items().SelectMany(delegate(Dictionary<string, object> item) { return A(V(item, "Actions")); }).Select(D).Where(delegate(Dictionary<string, object> action) { return action != null && (!suggested || B(action, "Suggested")); }).GroupBy(ActionKey).Select(delegate(IGrouping<string, Dictionary<string, object>> group) { return group.First(); }).ToList();
        }
        private static string ActionKey(Dictionary<string, object> action) { return S(action, "Id") + ":" + S(action, "AdapterId"); }
        private string PlainSummary(Dictionary<string, object> item)
        {
            if (item == null) return T(busy == "diagnose" ? "checkingSummary" : "awaitingScan");
            if (A(V(item, "Actions")).Any(delegate(object a) { Dictionary<string, object> action = D(a); return S(action, "Id") == "DisableProxy" && B(action, "Suggested"); })) return T("simpleProxyFound");
            return T(N(item, "State") == 0 ? "simplePassed" : N(item, "State") == 3 ? "simpleUncertain" : "simple" + S(item, "Id"));
        }
        private string Status(Dictionary<string, object> item) { return T(item == null ? busy == "diagnose" ? "checking" : "pending" : new[] { "passed", "warning", "failed", "unknown" }[Math.Max(0, Math.Min(3, (int)N(item, "State")))]); }
        private Border StatusBadge(Dictionary<string, object> item)
        {
            bool checking = item == null && busy == "diagnose";
            int state = item == null ? 3 : Math.Max(0, Math.Min(3, (int)N(item, "State")));
            string foreground = checking ? "#3B46B9" : state == 0 ? "#28694C" : state == 1 ? "#865A18" : state == 2 ? "#9C3945" : "#62616B";
            string background = checking ? "#E3E3F2" : state == 0 ? "#E2EEE6" : state == 1 ? "#F3E8CF" : state == 2 ? "#F3DFE0" : "#E8E3DE";
            TextBlock label = Text(Status(item), 11.5, BrushOf(foreground), true); label.Margin = new Thickness(0); label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            return new Border { Child = label, Background = BrushOf(background), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4), MinWidth = 92, VerticalAlignment = VerticalAlignment.Center };
        }
        private string Online(object value) { return T(value is bool ? (bool)value ? "internetConfirmed" : "internetNotConfirmed" : "unknown"); }
        private string ConfigText(string value) { return value == "Automatic" ? T("automatic") : value == "Manual" ? T("manual") : value.Length == 0 ? T("emptyValue") : value; }
        private string SettingLabel(string key) { return T(key == "Proxy.Enable" ? "configProxyEnabled" : key == "Proxy.Server" ? "configProxyServer" : key == "Proxy.Pac" ? "configPac" : key == "Adapters.Active" ? "configActiveAdapters" : key.EndsWith("DnsMode", StringComparison.Ordinal) ? "configDnsMode" : key.EndsWith("DnsAddresses", StringComparison.Ordinal) || key.EndsWith("ConfiguredDns", StringComparison.Ordinal) ? "configDnsAddresses" : key.EndsWith("Addresses", StringComparison.Ordinal) ? "configAddresses" : "configSetting"); }
        private string T(string key) { return NativeUiText.LocalText(key); }
        private string U(string key) { return T(key); }
        private static bool TryUtc(object value, out DateTime utc)
        {
            utc = DateTime.MinValue; if (value == null) return false;
            if (value is DateTime) { utc = ((DateTime)value).ToUniversalTime(); return true; }
            string text = Convert.ToString(value, CultureInfo.InvariantCulture); Match match = Regex.Match(text, @"/Date\((-?\d+)"); long milliseconds;
            if (match.Success && Int64.TryParse(match.Groups[1].Value, out milliseconds)) { try { utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds); return true; } catch (ArgumentOutOfRangeException) { return false; } }
            DateTime parsed; if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return false; utc = parsed.ToUniversalTime(); return true;
        }
        private string DateText(object value)
        {
            if (value == null) return "—"; DateTime date;
            if (value is DateTime) date = (DateTime)value;
            else { string text = Convert.ToString(value, CultureInfo.InvariantCulture); Match match = Regex.Match(text, @"/Date\((-?\d+)"); long ms; if (match.Success && Int64.TryParse(match.Groups[1].Value, out ms)) { try { date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime(); } catch (ArgumentOutOfRangeException) { return "—"; } } else if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)) return "—"; }
            return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        private string Number(object value) { if (value == null) return "—"; double number; return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsInfinity(number) && !Double.IsNaN(number) ? number.ToString("0.0", CultureInfo.GetCultureInfo(language)) : "—"; }
        private Button MakeButton(string key, Action click, bool primary = false, bool tracked = true, Func<bool> enabled = null)
        {
            Button button = new Button { Content = T(key), Background = primary ? Purple : Back, Foreground = primary ? PrimaryInk : Ink, BorderBrush = primary ? Purple : Line };
            AutomationProperties.SetAutomationId(button, key); AutomationProperties.SetName(button, T(key)); button.Click += delegate { click(); }; if (tracked) Track(button, enabled); return button;
        }
        private Button MakeStartButton(string key, string operation, Action start)
        {
            Button button = MakeButton(key, delegate { if (NormalizedOperation(busy) == operation) Stop(); else if (busy == null) start(); }, true, false);
            operationButtons.Add(Tuple.Create(button, operation, key)); return button;
        }
        private void Track(Control control, Func<bool> enabled) { controls.Add(new KeyValuePair<Control, Func<bool>>(control, enabled)); control.IsEnabled = busy == null && (enabled == null || enabled()); }
        private TextBox Input(string value) { TextBox input = new TextBox { Text = value, MaxLength = 4096, Margin = new Thickness(0, 3, 0, 8), MinWidth = 130 }; Track(input, null); return input; }
        private ComboBox Choice(string[] keys, int selected) { ComboBox choice = new ComboBox { Margin = new Thickness(0, 3, 0, 8), MinWidth = 200 }; foreach (string key in keys) choice.Items.Add(T(key)); choice.SelectedIndex = Math.Max(0, selected); Track(choice, null); return choice; }
        private StackPanel Field(string key, Control control, double width = Double.NaN) { StackPanel field = new StackPanel { Margin = new Thickness(0, 5, 12, 5), Width = width }; field.Children.Add(Text(T(key) + (key == "monitorInterval" ? " (ms)" : ""), 12, Muted, true)); AutomationProperties.SetName(control, T(key)); field.Children.Add(control); return field; }
        private void AddMetric(Panel parent, string label, string value) { StackPanel box = new StackPanel { MinWidth = 165, Margin = new Thickness(0, 0, 12, 12) }; box.Children.Add(Text(label, 12, Muted)); box.Children.Add(Text(value, 25, Ink, true)); Border border = new Border { Background = BrushOf("#FFFFFF"), CornerRadius = new CornerRadius(15), BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(17, 14, 17, 7), Margin = new Thickness(0, 0, 10, 12), Child = box }; parent.Children.Add(border); }
        private StackPanel Card(Panel parent, string title, string summary, string background = null)
        {
            StackPanel content = new StackPanel(); Border border = new Border { Background = BrushOf(background ?? (theme == "dark" ? "#202635" : "#F4EEE8")), BorderBrush = BrushOf("#FFFBF8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(21, 18, 21, 17), Margin = new Thickness(0, 0, 0, 15), Child = content, Effect = new DropShadowEffect { Color = Color.FromRgb(178, 164, 151), Opacity = theme == "dark" ? 0.12 : 0.28, BlurRadius = 18, ShadowDepth = 5 } };
            if (!String.IsNullOrEmpty(title)) content.Children.Add(Text(title, 18, Ink, true)); if (!String.IsNullOrEmpty(summary)) { TextBlock description = Text(summary, 13, Muted); description.Margin = new Thickness(0, 7, 0, 13); content.Children.Add(description); } parent.Children.Add(border); return content;
        }
        private static TextBlock Text(string value, double size, Brush color, bool bold = false) { return new TextBlock { Text = value ?? "", FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) }; }
        private TextBox Output(string value, double minHeight, bool empty = false) { return new TextBox { Text = value ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, TextAlignment = empty ? TextAlignment.Center : TextAlignment.Left, VerticalContentAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MinHeight = minHeight, MaxHeight = Math.Max(minHeight, 430), Foreground = Ink, Background = BrushOf("#ECE5DE"), FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12, BorderThickness = new Thickness(0), Margin = new Thickness(0, 10, 0, 3), Padding = new Thickness(12) }; }
        private Brush BrushOf(string value)
        {
            if (theme == "dark")
            {
                switch (value.ToUpperInvariant())
                {
                    case "#25262C": value = "#E8ECF4"; break; case "#62616B": value = "#AAB4C5"; break; case "#3B46B9": value = "#8792FF"; break;
                    case "#F4EEE8": value = "#151922"; break; case "#F1EBE5": value = "#191E29"; break;
                    case "#ECE5DE": case "#F8F5F0": case "#FAFAFE": case "#FFFFFF": value = "#171C27"; break;
                    case "#DED7D0": case "#FFFBF8": case "#F0EFF5": value = "#303A4D"; break;
                    case "#E3E3F2": case "#ECE9F9": value = "#2C3554"; break; case "#C4C6E6": value = "#56628C"; break;
                    case "#E2EEE6": value = "#243D35"; break; case "#28694C": value = "#9DDEBB"; break;
                    case "#F3E8CF": case "#F9F5EA": value = "#423925"; break; case "#865A18": value = "#E9C77C"; break;
                    case "#F3DFE0": case "#FBEFEE": value = "#442C35"; break; case "#9C3945": case "#9B4B4B": value = "#F1AEB8"; break;
                    case "#E8E3DE": value = "#303746"; break; case "#168F97": value = "#69D4DC"; break; case "#C57B80": value = "#F1AEB8"; break;
                }
            }
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
        }
        private void ApplyPalette() { Ink = BrushOf("#25262C"); Muted = BrushOf("#62616B"); Purple = BrushOf("#3B46B9"); Line = BrushOf("#DED7D0"); Back = BrushOf("#F4EEE8"); Background = Back; Foreground = Ink; }
        private static BitmapSource LoadLogo()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "network-rescue-logo-256.png");
            if (!File.Exists(path)) return null;
            try { BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); return image; }
            catch (IOException) { return null; } catch (NotSupportedException) { return null; } catch (FormatException) { return null; }
        }
        private static object V(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value : null; }
        private static Dictionary<string, object> D(object value) { return value as Dictionary<string, object>; }
        private static object[] A(object value) { object[] array = value as object[]; if (array != null) return array; ArrayList list = value as ArrayList; return list == null ? new object[0] : list.ToArray(); }
        private static string S(Dictionary<string, object> data, string key) { return Convert.ToString(V(data, key), CultureInfo.InvariantCulture) ?? ""; }
        private static bool B(Dictionary<string, object> data, string key) { return V(data, key) is bool && (bool)V(data, key); }
        private static double N(Dictionary<string, object> data, string key) { double value; return Double.TryParse(S(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0; }
        private static string Empty(string value) { return String.IsNullOrEmpty(value) ? null : value; }
        private static Dictionary<string, object> Values(params object[] values) { Dictionary<string, object> result = new Dictionary<string, object>(); for (int i = 0; i + 1 < values.Length; i += 2) result.Add((string)values[i], values[i + 1]); return result; }
        private static void ReplaceResult(List<Dictionary<string, object>> list, Dictionary<string, object> value, string key) { if (value == null) return; int index = list.FindIndex(delegate(Dictionary<string, object> item) { return S(item, key) == S(value, key); }); if (index < 0) list.Add(value); else list[index] = value; }
    }
}

