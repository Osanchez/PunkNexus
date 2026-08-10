using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PunkNexus.Services;

/// <summary>
/// A file-driven remote control for this window, so the Play flow can be tested without a human
/// clicking it.
///
/// The parts of PUNK Nexus that most need testing are exactly the parts that only exist behind a
/// button: the mod swap moves a player's files around and promises to put them back, and until now
/// the only way to exercise it was by hand. Automating a desktop GUI from outside is unreliable
/// (it depends on window focus, screen coordinates and timing, and it can click whatever happens
/// to be under the cursor). Driving it from inside the app is exact: commands run on the UI thread
/// against the real visual tree, and a control that is not there is an error rather than a
/// mis-click on something else.
///
/// OFF unless PUNKNEXUS_DIAG=1 is in the environment. Not a setting, deliberately -- a settings
/// file can be edited by accident or carried between machines, whereas an environment variable is
/// set by whoever launches the process and disappears with it. A normal user never has this.
///
/// Protocol matches the game mod's devcmd harness, because the same person is driving both:
///   write a line to  %LocalAppData%\PunkNexus\devcmd.txt
///   read the answer from %LocalAppData%\PunkNexus\devout.txt
///
/// Commands:
///   uidump                 every interactive control: kind, id, text, enabled, visible
///   click &lt;id-or-text&gt;     invoke the first match (buttons, tabs, checkboxes, list rows)
///   settext &lt;id&gt; &lt;value&gt;   set a TextBox's text
///   tab &lt;name&gt;             select a tab by header
///   screenshot &lt;name&gt;      render THIS WINDOW to shots\&lt;name&gt;.png
///   dialog                 whether a modal is open, its title and its buttons
///   invoke &lt;text&gt; &lt;Cmd&gt;    run a bound command on the row containing &lt;text&gt;
///   waitfor &lt;text&gt; [secs]  wait until a control is clickable, instead of sleeping and hoping
///   state                  settings + install state + shelf, as the app sees them
///   quit                   close the app
/// </summary>
public sealed class DiagHarness
{
    private readonly Window _window;
    private readonly string _cmdFile;
    private readonly string _outFile;
    private readonly string _shotDir;
    private DispatcherTimer? _timer;
    private long _consumed;

    private DiagHarness(Window window)
    {
        _window = window;
        _cmdFile = Path.Combine(AppPaths.Root, "devcmd.txt");
        _outFile = Path.Combine(AppPaths.Root, "devout.txt");
        _shotDir = Path.Combine(AppPaths.Root, "shots");
    }

    /// <summary>Start the harness if the environment asks for it. Returns null otherwise.</summary>
    public static DiagHarness? MaybeStart(Window window)
    {
        var flag = Environment.GetEnvironmentVariable("PUNKNEXUS_DIAG");
        if (!string.Equals(flag, "1", StringComparison.Ordinal)) return null;

        var harness = new DiagHarness(window);
        harness.Start();
        return harness;
    }

    private void Start()
    {
        try
        {
            Directory.CreateDirectory(_shotDir);
            // Start from a clean slate so a command file left over from a previous run is not
            // replayed into a fresh window -- which would look like the app acting on its own.
            if (File.Exists(_cmdFile)) File.Delete(_cmdFile);
            File.WriteAllText(_outFile, $"diag harness ready ({DateTime.Now:HH:mm:ss})\n");
        }
        catch (Exception ex)
        {
            Log.Error("diag harness could not prepare its files", ex);
            return;
        }

        // Polled rather than FileSystemWatcher: a watcher fires mid-write and hands you half a
        // line. 250ms is far below human reaction time and costs nothing.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { Pump(); TickWait(); };
        _timer.Start();

        Log.Warn("DIAGNOSTIC MODE: this window can be driven from " + _cmdFile
                 + ". Unset PUNKNEXUS_DIAG to disable.");
    }

    private void Pump()
    {
        string[] lines;
        try
        {
            if (!File.Exists(_cmdFile)) return;
            var info = new FileInfo(_cmdFile);
            if (info.Length <= _consumed) return;                 // nothing new appended
            using var fs = new FileStream(_cmdFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_consumed, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _consumed = fs.Position;
        }
        catch { return; }                                          // mid-write; try again next tick

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try { Execute(line); }
            catch (Exception ex) { Out($"ERROR {line}: {ex.Message}"); }
        }
    }

    private void Execute(string line)
    {
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();

        switch (verb)
        {
            case "uidump": Out(Dump()); return;
            case "dialog": Out(DialogState()); return;
            case "invoke": Out(Invoke(rest)); return;
            case "waitfor": Out(BeginWait(rest)); return;
            case "click":
            {
                // Report the dialog state after the click as well. "click: 'Install'" only ever
                // said a button was invoked; it could not distinguish opening a prompt, dismissing
                // one, or hitting the wrong control entirely -- which is exactly how three stray
                // installs got started while a prompt sat unanswered.
                var before = OpenDialogTitle();
                var result = Click(rest);
                var after = OpenDialogTitle();
                if (before != after)
                    result += after is null ? "  [dialog closed]" : $"  [dialog now: {after}]";
                else if (after is not null)
                    result += $"  [dialog still: {after}]";
                Out(result);
                return;
            }
            case "tab": Out(SelectTab(rest)); return;
            case "settext": Out(SetText(rest)); return;
            case "screenshot": Out(Screenshot(rest)); return;
            case "state": Out(State()); return;
            case "quit":
                Out("quit: closing");
                Dispatcher.UIThread.Post(() => _window.Close());
                return;
            default: Out($"unknown command '{verb}'"); return;
        }
    }

    // ------------------------------------------------------------------ inspection

    /// <summary>
    /// A stable handle for a control. Name if the XAML gave it one, otherwise its visible text --
    /// which is what a person would say ("click Play"), and what stays meaningful across layout
    /// changes that renumber everything else.
    /// </summary>
    private static string IdOf(Control c)
    {
        if (!string.IsNullOrEmpty(c.Name)) return c.Name!;
        var text = TextOf(c);
        return string.IsNullOrEmpty(text) ? c.GetType().Name : text;
    }

    private static string TextOf(Control c) => c switch
    {
        // CheckBox : ToggleButton : Button in Avalonia, so the derived types come first or the
        // Button arm eats them.
        CheckBox cb => cb.Content?.ToString() ?? "",
        TabItem ti => ti.Header?.ToString() ?? "",
        Button b => b.Content?.ToString() ?? "",
        TextBox t => t.Text ?? "",
        TextBlock tb => tb.Text ?? "",
        ContentControl cc => cc.Content?.ToString() ?? "",
        _ => "",
    };

    private string Dump()
    {
        var sb = new StringBuilder("uidump:\n");
        var n = 0;
        foreach (var c in Interactive())
        {
            sb.Append("  ").Append(c.GetType().Name.PadRight(12))
              .Append(" id='").Append(IdOf(c)).Append('\'');
            var text = TextOf(c);
            if (!string.IsNullOrEmpty(text) && text != IdOf(c)) sb.Append(" text='").Append(text).Append('\'');
            sb.Append(" enabled=").Append(c.IsEnabled)
              .Append(" visible=").Append(c.IsEffectivelyVisible);
            if (c is TabItem { IsSelected: true }) sb.Append(" SELECTED");
            sb.Append('\n');
            n++;
        }
        sb.Append($"  {n} interactive control(s)");
        return sb.ToString();
    }

    /// <summary>Everything a person could act on. Visual-tree order, so it reads top to bottom.</summary>
    private IEnumerable<Control> Interactive() =>
        _window.GetVisualDescendants().OfType<Control>()
               .Where(c => c is Button or CheckBox or TextBox or TabItem or ComboBox or ListBoxItem);

    /// <summary>
    /// The named overlay, but only while it is actually on screen. Both modals are always in the
    /// tree and switch on IsVisible, so presence proves nothing on its own.
    /// </summary>
    private Control? VisibleOverlay(string name) =>
        _window.GetVisualDescendants().OfType<Control>()
               .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

    /// <summary>The title of the open modal, or null when none is showing.</summary>
    private string? OpenDialogTitle()
    {
        var overlay = VisibleOverlay("DialogOverlay");
        if (overlay is null) return null;

        var text = overlay.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return text ?? "(untitled)";
    }

    private string DialogState()
    {
        // Checked first, because by the time this one is up the dialog that authorised it has
        // closed -- so a harness that only knew about DialogOverlay would report "none open" for
        // the one state in which the window is least able to do anything else.
        if (VisibleOverlay("UpdateOverlay") is { } updating)
        {
            var lines = updating.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            // Declaration order: title, then the stage line. Buttons are empty because it has
            // none -- there is nothing to answer, only something to wait for.
            return $"dialog: UPDATING \"{lines.FirstOrDefault() ?? "(untitled)"}\" "
                   + $"stage=\"{lines.Skip(1).FirstOrDefault() ?? ""}\" buttons=[]";
        }

        var overlay = VisibleOverlay("DialogOverlay");
        if (overlay is null) return "dialog: none open";

        var title = OpenDialogTitle() ?? "(untitled)";
        // Only the dialog's own buttons. Listing the whole window made the answer useless -- the
        // nav and every row button appeared alongside the two that actually belong to the modal.
        var buttons = overlay.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.IsEnabled)
            .Select(IdOf);
        return $"dialog: OPEN \"{title}\" buttons=[{string.Join(", ", buttons)}]";
    }

    /// <summary>
    /// Run a named command on whatever row shows the given text.
    ///
    /// Not everything a person can click is a Button. The scan report opens by clicking the mod
    /// ROW -- a plain container with a bound gesture -- which no amount of button hunting will
    /// find. Rather than synthesise pointer events (which need real coordinates and hit-testing,
    /// and would reintroduce every problem clicking-by-position has), this walks up from the
    /// matched element to the first DataContext exposing that ICommand and executes it. Same code
    /// path the gesture would reach.
    /// </summary>
    private string Invoke(string rest)
    {
        var space = rest.LastIndexOf(' ');
        if (space <= 0) return "invoke: usage invoke <text> <CommandName>";
        var text = rest[..space].Trim();
        var commandName = rest[(space + 1)..].Trim();

        var match = _window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.IsEffectivelyVisible
                                 && TextOf(c).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (match is null) return $"invoke: nothing visible showing '{text}'";

        for (Control? node = match; node is not null; node = node.Parent as Control)
        {
            var context = node.DataContext;
            if (context is null) continue;

            var property = context.GetType().GetProperty(commandName);
            if (property?.GetValue(context) is not System.Windows.Input.ICommand command) continue;
            if (!command.CanExecute(null)) return $"invoke: {commandName} refused (CanExecute false)";

            command.Execute(null);
            return $"invoke: {commandName} on '{text}' ({context.GetType().Name})";
        }
        return $"invoke: found '{text}' but no ancestor exposes {commandName}";
    }

    // ------------------------------------------------------------------ waiting

    private string? _waitFor;
    private DateTime _waitUntil;

    /// <summary>
    /// Wait until a control matching the text is visible and enabled, then say so.
    ///
    /// The alternative was a driver sleeping a guessed number of seconds between steps, which is
    /// how a click landed before its dialog existed and hit whatever was underneath. Non-blocking
    /// by design: it is re-checked on the same timer that reads commands, so the UI keeps running
    /// while the wait is outstanding.
    /// </summary>
    private string BeginWait(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return "waitfor: usage waitfor <text> [seconds]";
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var seconds = 30.0;
        if (parts.Length > 1 && double.TryParse(parts[^1], out var parsed))
        {
            seconds = parsed;
            rest = string.Join(' ', parts[..^1]);
        }
        _waitFor = rest;
        _waitUntil = DateTime.UtcNow.AddSeconds(seconds);
        return $"waitfor: watching for '{rest}' (up to {seconds:0}s)";
    }

    private void TickWait()
    {
        if (_waitFor is null) return;

        if (Find(_waitFor) is not null)
        {
            var what = _waitFor;
            _waitFor = null;
            Out($"waitfor: '{what}' is ready");
            return;
        }
        if (DateTime.UtcNow > _waitUntil)
        {
            var what = _waitFor;
            _waitFor = null;
            Out($"waitfor: TIMED OUT waiting for '{what}'. {DialogState()}");
        }
    }

    // ------------------------------------------------------------------ actions

    private Control? Find(string idOrText)
    {
        if (string.IsNullOrWhiteSpace(idOrText)) return null;
        // IsEffectivelyVisible, not IsVisible. IsVisible is the control's OWN flag and stays true
        // inside a hidden parent, so every off-screen tab's controls looked clickable. Both Mods
        // and Servers have a "Refresh" button; matching on IsVisible found the Mods one while the
        // Servers tab was showing, and clicking it reported success having refreshed the wrong
        // list. Nothing about that is visible from the outside -- which is precisely why the
        // harness must not lie about what is on screen.
        var all = Interactive().Where(c => c.IsEffectivelyVisible && c.IsEnabled).ToList();

        // A modal is showing? Then it owns every click. Both the dialog and the rows behind it
        // have buttons reading "Install", and tree order put a ROW first -- so accepting a
        // verification prompt actually started installing some unrelated mod further down the
        // list, while the prompt sat there unanswered. A person cannot make that mistake, because
        // the overlay physically blocks the rows; the harness has to be told.
        var overlay = VisibleOverlay("DialogOverlay");
        if (overlay is not null)
        {
            var inDialog = all.Where(c => c.GetVisualAncestors().Contains(overlay)).ToList();
            if (inDialog.Count > 0) all = inDialog;
        }

        // The update overlay gets the same treatment for the opposite reason: it contains no
        // buttons at all, so the scoping above would find nothing to narrow to and leave every
        // control on the page behind it clickable. A person looking at it can click none of them.
        if (VisibleOverlay("UpdateOverlay") is not null) return null;
        return all.FirstOrDefault(c => string.Equals(IdOf(c), idOrText, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(c => string.Equals(TextOf(c), idOrText, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(c => TextOf(c).Contains(idOrText, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(c => string.Equals(c.GetType().Name, idOrText, StringComparison.OrdinalIgnoreCase));
    }

    private string Click(string target)
    {
        var c = Find(target);
        if (c == null) return $"click: no visible, enabled control matching '{target}'";

        switch (c)
        {
            // Again: the derived types before Button, or they never match.
            case CheckBox cb:
                cb.IsChecked = !(cb.IsChecked ?? false);
                return $"click: '{IdOf(c)}' -> {cb.IsChecked}";
            case RadioButton rb:
                // Set IsChecked rather than raising Click. This app's tab strip is RadioButtons
                // bound to a selection property, and a synthetic Click leaves that binding
                // untouched -- the command reported success while the view never changed, which is
                // the worst kind of test tooling.
                rb.IsChecked = true;
                return $"click: '{IdOf(c)}' selected";
            case Button b:
                // Prefer the bound command over a synthetic pointer event: it runs exactly what the
                // button would run, and cannot land on whatever happens to overlap it on screen.
                if (b.Command != null && b.Command.CanExecute(b.CommandParameter))
                    b.Command.Execute(b.CommandParameter);
                else
                    b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return $"click: '{IdOf(c)}' (Button)";
            case TabItem ti:
                ti.IsSelected = true;
                return $"click: tab '{IdOf(c)}'";
            case ListBoxItem li:
                li.IsSelected = true;
                return $"click: row '{IdOf(c)}'";
            default:
                c.Focus();
                return $"click: focused '{IdOf(c)}' ({c.GetType().Name})";
        }
    }

    private string SelectTab(string header)
    {
        var tab = Interactive().OfType<TabItem>()
            .FirstOrDefault(t => (t.Header?.ToString() ?? "").Contains(header, StringComparison.OrdinalIgnoreCase));
        if (tab == null)
            return "tab: no such tab. Have: "
                 + string.Join(", ", Interactive().OfType<TabItem>().Select(t => t.Header?.ToString()));
        tab.IsSelected = true;
        return $"tab: '{tab.Header}' selected";
    }

    private string SetText(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return "settext: usage settext <id> [value]";
        var space = rest.IndexOf(' ');
        var id = space < 0 ? rest : rest[..space];
        var value = space < 0 ? "" : rest[(space + 1)..];   // no value = clear the field
        if (Find(id) is not TextBox box) return $"settext: no TextBox matching '{id}'";
        box.Text = value;
        return $"settext: '{id}' = '{value}'";
    }

    /// <summary>
    /// Render THIS WINDOW to a PNG. Avalonia draws the window's own visual tree into a bitmap, so
    /// the file can only ever contain this application -- never another window, and never whatever
    /// else is on the desktop. That property is the whole reason to do it this way.
    /// </summary>
    private string Screenshot(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "shot";
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_shotDir, safe + ".png");

        var size = _window.ClientSize;
        if (size.Width < 1 || size.Height < 1) return "screenshot: window has no size yet";

        var pixel = new PixelSize((int)size.Width, (int)size.Height);
        using var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96));
        bitmap.Render(_window);
        bitmap.Save(path);
        return $"screenshot: {path}";
    }

    private string State()
    {
        var sb = new StringBuilder("state:\n");
        try
        {
            sb.Append("  settings: ").Append(File.Exists(AppPaths.SettingsFile)
                ? File.ReadAllText(AppPaths.SettingsFile).Replace("\n", " ").Replace("\r", "")
                : "(none)").Append('\n');

            if (Directory.Exists(AppPaths.InstallsDir))
                foreach (var f in Directory.GetFiles(AppPaths.InstallsDir, "*.json"))
                    sb.Append("  install ").Append(Path.GetFileName(f)).Append(": ")
                      .Append(File.ReadAllText(f).Replace("\n", " ").Replace("\r", "")).Append('\n');
            else sb.Append("  installs: (none)\n");
        }
        catch (Exception ex) { sb.Append("  ERROR ").Append(ex.Message).Append('\n'); }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ output

    private void Out(string text)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {text}";
        try { File.AppendAllText(_outFile, stamped + "\n"); } catch { }
        Log.Info("diag: " + text.Split('\n')[0]);
    }
}
