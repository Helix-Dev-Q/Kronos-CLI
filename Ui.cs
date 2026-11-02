using System.Text;

namespace KronosCLI;

internal static class Ui
{
    public enum Level { Plain, Info, Ok, Warn, Error, Detail, Proxy }

    public sealed record Button(string Label, string? Value = null, Func<string?>? Action = null);

    public sealed record MenuItem(string Key, string Label, string Description, string? Short = null);

    readonly record struct Entry(DateTime Time, Level Level, string Text, string? Topic, bool Keep);
    readonly record struct Span(string Text, string Fg, string? Bg = null);

    enum HitKind { Command, Button, Tab, Follow, Body, Bar }
    readonly record struct Hit(int Row, int Left, int Right, HitKind Kind, int Pane = 0, string? Value = null, Button? Button = null, int Item = -1);

    const string Esc = "\x1b";
    const string Reset = Esc + "[0m";
    const string MouseOn = Esc + "[?1000h" + Esc + "[?1002h" + Esc + "[?1006h";
    const string MouseOff = Esc + "[?1006l" + Esc + "[?1002l" + Esc + "[?1000l";

    static readonly string Backdrop = Bg(9, 9, 14);
    static readonly string Panel = Bg(17, 17, 25);
    static readonly string Side = Bg(23, 23, 35);
    static readonly string ChipBg = Bg(40, 40, 58);
    static readonly string SelectBg = Bg(50, 43, 94);
    static readonly string KeyBg = Bg(205, 205, 222);
    static readonly string AccentBg = Bg(139, 123, 255);
    static readonly string GreenBg = Bg(96, 214, 140);
    static readonly string AmberBg = Bg(242, 192, 82);
    static readonly string RedBg = Bg(244, 108, 108);

    static readonly string Ink = Fg(12, 12, 20);
    static readonly string Border = Fg(58, 58, 84);
    static readonly string Faint = Fg(100, 100, 124);
    static readonly string Dim = Fg(150, 150, 174);
    static readonly string Text = Fg(214, 214, 228);
    static readonly string Bright = Fg(248, 248, 252);
    static readonly string Accent = Fg(162, 148, 255);
    static readonly string Green = Fg(96, 214, 140);
    static readonly string Amber = Fg(242, 192, 82);
    static readonly string Red = Fg(244, 108, 108);
    static readonly string Blue = Fg(112, 182, 246);
    static readonly string WarnText = Fg(248, 220, 156);
    static readonly string ErrorText = Fg(250, 176, 176);

    const int MaxLines = 600;
    const int MarginX = 2;
    const int MarginY = 1;
    const int Indent = 12;
    const int Col0 = MarginX + 2;
    const int FixedRows = 10;
    const int SidebarWidth = 30;
    const int SidebarMinInner = 92;

    const string ActivityEmpty = "Nothing yet - what Kronos and the game do shows up here";
    const string ProxyEmpty = "Proxy traffic shows up here once a client launches with the proxy";

    static readonly object sync = new();
    static readonly object draw = new();
    static readonly List<Entry> activity = [];
    static readonly List<Entry> proxyLines = [];
    static readonly List<(string Key, string Value, Level Kind)> status = [];

    static string menuTitle = "";
    static List<MenuItem> menu = [];
    static int selected;
    static string next = "";
    static Level nextLevel = Level.Info;

    static string inputLabel = "";
    static string inputExample = "";
    static string input = "";
    static IReadOnlyList<Button> inputButtons = [];
    static bool prompting;
    static bool commandMode;
    static bool secret;

    static int focus;
    static readonly int[] offset = new int[2];
    static readonly int[] maxOffset = new int[2];
    static readonly int[] paneTop = [0, 0];
    static readonly int[] paneHeight = [1, 1];
    static int lastPaneWidth = 80;
    static List<Hit> hits = [];

    static bool mouse;
    static bool leftDown;
    static int dragPane = -1;

    static Thread? renderThread;
    static volatile bool running;
    static volatile bool dirty = true;
    static int lastWidth, lastHeight, lastSecond = -1;

    public static bool Active => running;

    public static void Start()
    {
        if (running) return;
        running = true;
        try { System.Console.OutputEncoding = Encoding.UTF8; } catch { }
        Write(Esc + "[?1049h" + Esc + "[?25l" + Esc + "[?7l" + Backdrop + Esc + "[2J");
        mouse = Console.TryEnableMouse();
        if (mouse) Write(MouseOn);
        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "ui" };
        renderThread.Start();
    }

    public static void Stop()
    {
        if (!running) return;
        running = false;
        try { renderThread?.Join(400); } catch { }
        Write(Reset + (mouse ? MouseOff : "") + Esc + "[?7h" + Esc + "[?25h" + Esc + "[?1049l");
        Console.RestoreInput();
    }

    public static void Add(Level level, string message, string? topic = null, bool keep = false)
    {
        bool isProxy = message.StartsWith("Proxy:", StringComparison.Ordinal);
        if (!isProxy && level == Level.Detail) return;

        string text = isProxy ? message[6..].TrimStart() : message;
        Level shown = isProxy && level is Level.Plain or Level.Info ? Level.Proxy : level;

        lock (sync)
        {
            int pane = isProxy ? 1 : 0;
            var list = isProxy ? proxyLines : activity;
            var entry = new Entry(DateTime.Now, shown, text, topic, keep);
            list.Add(entry);
            if (list.Count > MaxLines) list.RemoveRange(0, list.Count - MaxLines);
            if (offset[pane] > 0) offset[pane] += Wrap(entry.Text, MessageWidth(lastPaneWidth)).Count;
        }
        dirty = true;
    }

    public static void Resolve(string topic)
    {
        int removed;
        lock (sync)
        {
            static bool Stale(Entry entry, string topic) => entry.Topic == topic && entry.Level is Level.Warn or Level.Error;
            removed = activity.RemoveAll(e => Stale(e, topic)) + proxyLines.RemoveAll(e => Stale(e, topic));
        }
        if (removed > 0) dirty = true;
    }

    public static void SetStatus(string key, string value, Level kind)
    {
        lock (sync)
        {
            int i = status.FindIndex(s => s.Key == key);
            if (i >= 0) status[i] = (key, value, kind);
            else status.Add((key, value, kind));
        }
        dirty = true;
    }

    public static void RemoveStatus(string key)
    {
        lock (sync) status.RemoveAll(s => s.Key == key);
        dirty = true;
    }

    public static void SetMenu(string title, IReadOnlyList<MenuItem> items, int? select = null)
    {
        lock (sync)
        {
            if (title != menuTitle) selected = 0;
            menuTitle = title;
            menu = [.. items];
            if (select is { } index) selected = index;
            selected = Math.Clamp(selected, 0, Math.Max(0, menu.Count - 1));
        }
        dirty = true;
    }

    public static void SetNext(string text, Level level)
    {
        lock (sync)
        {
            next = text;
            nextLevel = level;
        }
        dirty = true;
    }

    public static void ClearSession()
    {
        lock (sync)
        {
            activity.RemoveAll(e => !e.Keep);
            proxyLines.RemoveAll(e => !e.Keep);
            offset[0] = offset[1] = 0;
        }
        dirty = true;
    }

    public static string ReadCommand() => Read("› ", false, "", null, command: true);

    public static string ReadLine(string label, bool secret = false, string example = "", IReadOnlyList<Button>? buttons = null) =>
        Read(label, secret, example, buttons, command: false);

    static string Read(string label, bool secret, string example, IReadOnlyList<Button>? buttons, bool command)
    {
        lock (sync)
        {
            inputLabel = label;
            inputExample = example;
            input = "";
            inputButtons = buttons ?? [];
            Ui.secret = secret;
            commandMode = command;
            prompting = true;
        }
        dirty = true;

        string? chosen = mouse ? ReadWithMouse() : ReadWithKeys();

        string result;
        lock (sync)
        {
            result = (chosen ?? input).Trim();
            prompting = false;
            commandMode = false;
            inputLabel = "";
            inputExample = "";
            input = "";
            inputButtons = [];
        }
        dirty = true;
        return result;
    }

    static string? ReadWithKeys()
    {
        while (running)
        {
            ConsoleKeyInfo key;
            try { key = System.Console.ReadKey(true); }
            catch { Thread.Sleep(50); continue; }

            string? done;
            lock (sync) done = OnKey((int)key.Key, key.KeyChar, 1);
            dirty = true;
            if (done != null) return done;
        }
        return null;
    }

    static string? ReadWithMouse()
    {
        var pending = PendingKeys();
        var buffer = new Console.InputRecord[32];

        while (running)
        {
            if (pending.Count == 0)
            {
                int read = Console.ReadInput(buffer);
                if (read <= 0) { Thread.Sleep(20); continue; }
                for (int i = 0; i < read; i++) pending.Enqueue(buffer[i]);
            }

            var record = pending.Dequeue();
            if (record.EventType == Console.KeyEvent)
            {
                var key = record.Key;
                if (key.KeyDown == 0) continue;
                string? done;
                lock (sync) done = OnKey(key.VirtualKey, (char)key.Char, Math.Max(1, (int)key.RepeatCount));
                dirty = true;
                if (done != null) return done;
            }
            else if (record.EventType == Console.MouseEvent && OnMouse(record.Mouse) is { } hit)
            {
                if (hit.Kind == HitKind.Command) return hit.Value;
                if (hit.Button?.Value is { } value) return value;
                if (hit.Button?.Action is { } action)
                {
                    string? picked = action();
                    if (picked != null) return picked;
                    pending = PendingKeys();
                }
            }
        }
        return null;
    }

    static Queue<Console.InputRecord> PendingKeys()
    {
        leftDown = false;
        dragPane = -1;

        var keys = new Queue<Console.InputRecord>();
        var buffer = new Console.InputRecord[64];
        while (Console.PendingInput() > 0)
        {
            int read = Console.ReadInput(buffer);
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
                if (buffer[i].EventType == Console.KeyEvent) keys.Enqueue(buffer[i]);
        }
        return keys;
    }

    static string? OnKey(int virtualKey, char ch, int repeat)
    {
        bool picking = commandMode && menu.Count > 0;
        switch (virtualKey)
        {
            case 0x0D: return picking && input.Trim().Length == 0 ? menu[selected].Key : input;
            case 0x08: for (int i = 0; i < repeat && input.Length > 0; i++) input = input[..^1]; break;
            case 0x1B:
                if (commandMode && input.Length == 0) return "";
                input = "";
                break;
            case 0x09: focus = 1 - focus; break;
            case 0x21: ScrollPane(focus, repeat * Math.Max(1, paneHeight[focus] - 1)); break;
            case 0x22: ScrollPane(focus, -repeat * Math.Max(1, paneHeight[focus] - 1)); break;
            case 0x26: if (picking) MoveSelection(-repeat); else ScrollPane(focus, repeat); break;
            case 0x28: if (picking) MoveSelection(repeat); else ScrollPane(focus, -repeat); break;
            case 0x24: offset[focus] = maxOffset[focus]; break;
            case 0x23: offset[focus] = 0; break;
            default:
                if (ch == '\0' || char.IsControl(ch)) break;
                input += new string(ch, repeat);
                if (picking && menu.FindIndex(item => item.Key == input.Trim()) is >= 0 and var typed) selected = typed;
                break;
        }
        return null;
    }

    static void MoveSelection(int delta) => selected = ((selected + delta) % menu.Count + menu.Count) % menu.Count;

    static Hit? OnMouse(Console.MouseRecord record)
    {
        const uint Moved = 0x1, Wheeled = 0x4, Horizontal = 0x8;
        int x = record.X - WindowLeft();
        int y = record.Y - WindowTop();
        bool left = (record.Buttons & 1) != 0;

        lock (sync)
        {
            if ((record.Flags & Wheeled) != 0)
            {
                ScrollPane(PaneAt(y) ?? focus, (short)(record.Buttons >> 16) > 0 ? 3 : -3);
                dirty = true;
                return null;
            }
            if ((record.Flags & Horizontal) != 0) return null;
            if ((record.Flags & Moved) != 0)
            {
                if (left && dragPane >= 0)
                {
                    ScrollToBar(dragPane, y);
                    dirty = true;
                }
                else if (HitAt(x, y) is { Kind: HitKind.Command, Item: >= 0 } hover && hover.Item != selected)
                {
                    selected = hover.Item;
                    dirty = true;
                }
                return null;
            }

            bool pressed = left && !leftDown;
            leftDown = left;
            if (!left) dragPane = -1;
            if (!pressed) return null;

            if (HitAt(x, y) is not { } target) return null;

            dirty = true;
            switch (target.Kind)
            {
                case HitKind.Tab:
                case HitKind.Body:
                    focus = target.Pane;
                    return null;
                case HitKind.Follow:
                    focus = target.Pane;
                    offset[target.Pane] = 0;
                    return null;
                case HitKind.Bar:
                    focus = target.Pane;
                    dragPane = target.Pane;
                    ScrollToBar(target.Pane, y);
                    return null;
                default:
                    return target;
            }
        }
    }

    static Hit? HitAt(int x, int y)
    {
        Hit? found = null;
        foreach (var hit in hits)
            if (hit.Row == y && x >= hit.Left && x < hit.Right) found = hit;
        return found;
    }

    static int? PaneAt(int y)
    {
        for (int pane = 0; pane < 2; pane++)
            if (y >= paneTop[pane] - 1 && y < paneTop[pane] + paneHeight[pane]) return pane;
        return null;
    }

    static void ScrollPane(int pane, int rows) => offset[pane] = Math.Clamp(offset[pane] + rows, 0, maxOffset[pane]);

    static void ScrollToBar(int pane, int y)
    {
        int count = paneHeight[pane];
        double fraction = count <= 1 ? 1 : Math.Clamp((double)(y - paneTop[pane]) / (count - 1), 0, 1);
        offset[pane] = maxOffset[pane] - (int)Math.Round(fraction * maxOffset[pane]);
    }

    static void RenderLoop()
    {
        while (running)
        {
            int w = SafeWidth(), h = SafeHeight();
            int second = DateTime.Now.Second;

            if (dirty || w != lastWidth || h != lastHeight || second != lastSecond)
            {
                if (w != lastWidth || h != lastHeight) { try { System.Console.Clear(); } catch { } }
                lastWidth = w; lastHeight = h; lastSecond = second; dirty = false;
                try { Write(Compose(w, h)); } catch { }
            }
            Thread.Sleep(30);
        }
    }

    internal static string Compose(int width, int height)
    {
        int w = Math.Max(56, width);
        int h = Math.Max(18, height);
        int cardW = w - 2 * MarginX;
        int inner = cardW - 4;

        List<Entry> act, prx;
        List<(string Key, string Value, Level Kind)> st;
        List<MenuItem> items;
        List<string> promptLines;
        IReadOnlyList<Button> buttons;
        string title, example, shownInput, nextText;
        Level nextKind;
        bool prompt, command;
        int focused, chosen, leftWidth;
        lock (sync)
        {
            st = [.. status];
            items = [.. menu];
            title = menuTitle;
            chosen = selected;
            nextText = next;
            nextKind = nextLevel;
            example = inputExample;
            shownInput = secret ? new string('•', input.Length) : input;
            prompt = prompting;
            command = commandMode;
            buttons = inputButtons;

            int buttonsWidth = buttons.Sum(b => b.Label.Length + 2) + Math.Max(0, buttons.Count - 1);
            leftWidth = Math.Max(8, inner - (buttonsWidth > 0 ? buttonsWidth + 1 : 0));
            promptLines = PromptLines(inputLabel, shownInput, prompt, leftWidth, h);
            act = [.. activity];
            prx = [.. proxyLines];
            focused = focus;
        }

        int bodyRows = Math.Max(4, h - FixedRows - Math.Max(0, promptLines.Count - 2));
        bool sidebar = items.Count > 0 && inner >= SidebarMinInner && bodyRows >= items.Count + 1;
        int paneW = sidebar ? inner - SidebarWidth - 3 : inner;
        int paneX = Col0 + (sidebar ? SidebarWidth + 3 : 0);
        int contentRows = bodyRows - 2;
        int proxyH = Math.Max(1, contentRows / 3);
        int activityH = Math.Max(1, contentRows - proxyH);

        var actRows = PaneRows(act, paneW);
        var prxRows = PaneRows(prx, paneW);

        int actOffset, prxOffset;
        lock (sync)
        {
            paneHeight[0] = activityH;
            paneHeight[1] = proxyH;
            maxOffset[0] = Math.Max(0, actRows.Count - activityH);
            maxOffset[1] = Math.Max(0, prxRows.Count - proxyH);
            actOffset = offset[0] = Math.Min(offset[0], maxOffset[0]);
            prxOffset = offset[1] = Math.Min(offset[1], maxOffset[1]);
            lastPaneWidth = paneW;
        }

        string label = promptLines[^1];
        if (command && shownInput.Length == 0 && items.Count > 0)
            example = $"Enter = {items[Math.Clamp(chosen, 0, items.Count - 1)].Label}   or type a number";
        int room = leftWidth - 1 - label.Length;
        if (shownInput.Length > room) shownInput = room > 1 ? "…" + shownInput[^(room - 1)..] : "";

        var rows = new List<string>(h);
        var newHits = new List<Hit>();
        for (int i = 0; i < MarginY; i++) rows.Add(MarginRow(w));
        rows.Add(TopBorder(cardW));
        rows.Add(Card(BrandSpans(inner), inner));
        rows.Add(Card(StatusSpans(st), inner));
        rows.Add(Card(NextSpans(nextText, nextKind), inner));

        int bodyTop = rows.Count;
        var paneLines = new List<List<Span>>(bodyRows);
        int actTop = AddPane(paneLines, newHits, bodyTop, paneX, "ACTIVITY", 0, focused == 0, actRows, activityH, actOffset, paneW, ActivityEmpty);
        int prxTop = AddPane(paneLines, newHits, bodyTop, paneX, "PROXY", 1, focused == 1, prxRows, proxyH, prxOffset, paneW, ProxyEmpty);

        var itemLines = new List<int>();
        var sideLines = sidebar ? SidebarLines(title, items, chosen, command, paneLines.Count, itemLines) : null;
        for (int i = 0; i < paneLines.Count; i++)
        {
            var line = paneLines[i];
            if (sideLines != null) line = [.. sideLines[i], new Span(" │ ", Border), .. line];
            rows.Add(Card(line, inner));
        }
        if (sidebar && command)
            for (int i = 0; i < itemLines.Count; i++)
                newHits.Add(new Hit(bodyTop + itemLines[i], Col0, Col0 + SidebarWidth, HitKind.Command, Value: items[i].Key, Item: i));

        string rule = sidebar ? new string('─', SidebarWidth + 1) + "┴" + new string('─', paneW + 1) : new string('─', inner);
        rows.Add(Card([new Span(rule, Border)], inner));

        if (promptLines.Count > 1)
        {
            foreach (var line in promptLines.Take(promptLines.Count - 1)) rows.Add(Card([new Span(line, Text)], inner));
        }
        else
        {
            int hintRow = rows.Count;
            rows.Add(Card(sidebar || items.Count == 0 || !command
                ? KeyHelp(prompt, command, buttons.Count > 0)
                : MenuChips(items, chosen, inner, hintRow, newHits), inner));
        }

        int inputRow = rows.Count;
        var inputSpans = Fit(InputSpans(label, shownInput, example, prompt, command), leftWidth);
        if (buttons.Count > 0)
        {
            int pos = leftWidth + 1;
            inputSpans.Add(new Span(" ", Faint));
            for (int i = 0; i < buttons.Count; i++)
            {
                if (i > 0) { inputSpans.Add(new Span(" ", Faint)); pos++; }
                string chip = $" {buttons[i].Label} ";
                inputSpans.Add(new Span(chip, Ink, i == 0 ? AccentBg : KeyBg));
                newHits.Add(new Hit(inputRow, Col0 + pos, Col0 + pos + chip.Length, HitKind.Button, Button: buttons[i]));
                pos += chip.Length;
            }
        }
        rows.Add(Card(inputSpans, inner));
        rows.Add(BottomBorder(cardW));
        for (int i = 0; i < MarginY; i++) rows.Add(MarginRow(w));

        lock (sync)
        {
            paneTop[0] = actTop;
            paneTop[1] = prxTop;
            hits = newHits;
        }

        var sb = new StringBuilder();
        sb.Append(Esc + "[?25l" + Esc + "[?7l");
        for (int i = 0; i < rows.Count; i++)
            sb.Append($"{Esc}[{i + 1};1H").Append(rows[i]);

        if (prompt)
        {
            int col = Col0 + 1 + label.Length + shownInput.Length;
            sb.Append($"{Esc}[{inputRow + 1};{col}H{Esc}[?25h");
        }
        return sb.ToString();
    }

    static int AddPane(List<List<Span>> lines, List<Hit> newHits, int bodyTop, int paneX, string title, int pane, bool focused,
        List<Span[]> content, int count, int offset, int width, string empty)
    {
        int tab = lines.Count;
        lines.Add(TabSpans(title, focused, offset, width, out int noteStart, out int noteLength));
        newHits.Add(new Hit(bodyTop + tab, paneX, paneX + width, HitKind.Tab, pane));
        if (offset > 0 && noteLength > 0)
            newHits.Add(new Hit(bodyTop + tab, paneX + noteStart, paneX + noteStart + noteLength, HitKind.Follow, pane));

        int top = lines.Count;
        AppendPane(lines, content, count, offset, width, empty);
        for (int row = top; row < top + count; row++)
        {
            newHits.Add(new Hit(bodyTop + row, paneX, paneX + width - 2, HitKind.Body, pane));
            newHits.Add(new Hit(bodyTop + row, paneX + width - 2, paneX + width + 2, HitKind.Bar, pane));
        }
        return bodyTop + top;
    }

    static List<Span[]> PaneRows(List<Entry> entries, int width) =>
        entries.SelectMany(entry => LineRows(entry, MessageWidth(width))).ToList();

    static int MessageWidth(int width) => Math.Max(1, width - 2 - Indent);

    static void AppendPane(List<List<Span>> rows, List<Span[]> lines, int count, int offset, int width, string empty)
    {
        int content = width - 2;
        int end = lines.Count - offset;
        int start = Math.Max(0, end - count);
        int pad = count - (end - start);

        bool scrollable = lines.Count > count;
        int thumbSize = scrollable ? Math.Max(1, count * count / lines.Count) : 0;
        int thumbTop = scrollable ? (int)Math.Round((double)start / (lines.Count - count) * (count - thumbSize)) : 0;

        for (int i = 0; i < count; i++)
        {
            var spans = new List<Span>();
            int used = 0;
            if (i >= pad)
            {
                foreach (var span in lines[start + i - pad])
                {
                    spans.Add(span);
                    used += span.Text.Length;
                }
            }
            else if (lines.Count == 0 && i == 0)
            {
                string note = Clip(empty, content);
                spans.Add(new Span(note, Faint));
                used = note.Length;
            }

            spans.Add(new Span(new string(' ', Math.Max(0, content - used) + 1), Faint));
            spans.Add(!scrollable ? new Span(" ", Faint)
                : i >= thumbTop && i < thumbTop + thumbSize ? new Span("┃", Accent)
                : new Span("│", Border));
            rows.Add(spans);
        }
    }

    static IEnumerable<Span[]> LineRows(Entry line, int messageWidth)
    {
        var parts = Wrap(line.Text, messageWidth);
        string color = TextColor(line.Level);

        yield return
        [
            new Span(line.Time.ToString("HH:mm:ss"), Faint),
            new Span("  ", Faint),
            GlyphSpan(line.Level),
            new Span(" ", Faint),
            new Span(parts[0], color),
        ];
        for (int i = 1; i < parts.Count; i++)
            yield return [new Span(new string(' ', Indent), Faint), new Span(parts[i], color)];
    }

    static List<List<Span>> SidebarLines(string title, List<MenuItem> items, int chosen, bool command, int count, List<int> itemLines)
    {
        var lines = new List<List<Span>>();
        void Add(List<Span> spans) => lines.Add(Fit(spans, SidebarWidth, Side));

        Add([new Span($" {title.ToUpperInvariant()} ", command ? Ink : Dim, command ? AccentBg : ChipBg)]);
        if (count >= items.Count + 2) Add([]);

        for (int i = 0; i < items.Count; i++)
        {
            itemLines.Add(lines.Count);
            if (i == chosen)
                lines.Add(Fit(
                [
                    new Span("▌", Accent, SelectBg),
                    new Span($" {items[i].Key} ", Ink, command ? AccentBg : KeyBg),
                    new Span($" {items[i].Label}", Bright, SelectBg),
                ], SidebarWidth, SelectBg));
            else
                Add(
                [
                    new Span(" ", Faint),
                    new Span($" {items[i].Key} ", command ? Text : Faint, ChipBg),
                    new Span($" {items[i].Label}", command ? Text : Faint),
                ]);
        }

        if (chosen >= 0 && chosen < items.Count && lines.Count < count - 1)
        {
            Add([]);
            foreach (string part in Wrap(items[chosen].Description, SidebarWidth - 2))
            {
                if (lines.Count >= count) break;
                Add([new Span(" " + part, Dim)]);
            }
        }

        while (lines.Count < count) Add([]);
        return lines.Take(count).ToList();
    }

    static List<Span> MenuChips(List<MenuItem> items, int chosen, int width, int row, List<Hit> newHits)
    {
        int Needed(int gap) => items.Sum(item => item.Key.Length + 2 + 1 + (item.Short ?? item.Label).Length + gap);
        int spacing = Needed(2) <= width ? 2 : Needed(1) <= width ? 1 : -1;

        var spans = new List<Span>();
        int pos = 0;
        for (int i = 0; i < items.Count; i++)
        {
            string chip = $" {items[i].Key} ";
            string text = spacing < 0
                ? i == chosen ? $" {items[i].Short ?? items[i].Label} " : " "
                : $" {items[i].Short ?? items[i].Label}{new string(' ', spacing)}";
            bool on = i == chosen;
            spans.Add(new Span(chip, Ink, on ? AccentBg : KeyBg));
            spans.Add(new Span(text, on ? Bright : Dim));
            newHits.Add(new Hit(row, Col0 + pos, Col0 + pos + chip.Length + text.TrimEnd().Length, HitKind.Command, Value: items[i].Key, Item: i));
            pos += chip.Length + text.Length;
        }
        return spans;
    }

    static List<Span> KeyHelp(bool prompt, bool command, bool hasButtons)
    {
        (string Key, string What)[] keys = command
            ? [("↑↓", "choose"), ("Enter", "select"), ("Esc", "back"), ("?", "help"), ("Tab", "switch pane"), ("wheel/PgUp/PgDn", "scroll")]
            : prompt
                ? [("Enter", "confirm"), ("empty Enter", "keeps what's shown in [ ]"), ("Esc", "clear typing"), .. hasButtons ? [("click", "a button on the right")] : Array.Empty<(string, string)>()]
                : [("", "working…")];

        var spans = new List<Span>();
        foreach (var (key, what) in keys)
        {
            if (key.Length > 0) spans.Add(new Span(key, Accent));
            spans.Add(new Span($" {what}    ", Dim));
        }
        return spans;
    }

    static List<string> PromptLines(string label, string value, bool prompting, int width, int height)
    {
        if (!prompting) return [label];

        var lines = Wrap(label, width);
        if (lines[^1].Length + value.Length >= width) lines.Add("");

        int maxExtra = Math.Max(0, height - 2 * MarginY - 12);
        if (lines.Count - 2 > maxExtra) lines = [.. lines.Take(maxExtra + 1), lines[^1]];
        return lines;
    }

    static List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            string rest = paragraph;
            while (rest.Length > width)
            {
                int cut = rest.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                lines.Add(rest[..cut]);
                rest = rest[cut..].TrimStart(' ');
            }
            lines.Add(rest);
        }
        return lines;
    }

    static List<Span> Fit(IEnumerable<Span> spans, int width, string? bg = null)
    {
        var fitted = new List<Span>();
        int used = 0;
        foreach (var span in spans)
        {
            if (used >= width) break;
            string text = used + span.Text.Length > width ? Clip(span.Text, width - used) : span.Text;
            fitted.Add(span with { Text = text, Bg = span.Bg ?? bg });
            used += text.Length;
        }
        if (used < width) fitted.Add(new Span(new string(' ', width - used), Faint, bg));
        return fitted;
    }

    static List<Span> BrandSpans(int inner)
    {
        string clock = DateTime.Now.ToString("HH:mm:ss");
        var spans = new List<Span>
        {
            new(" ◆ KRONOS ", Ink, AccentBg),
            new(" CLI ", Accent, ChipBg),
            new("   OGFN launcher", Faint),
        };
        int used = spans.Sum(s => s.Text.Length);
        spans.Add(new Span(new string(' ', Math.Max(1, inner - used - clock.Length)), Faint));
        spans.Add(new Span(clock, Dim));
        return spans;
    }

    static List<Span> StatusSpans(List<(string Key, string Value, Level Kind)> badges)
    {
        var spans = new List<Span>();
        if (badges.Count == 0)
        {
            spans.Add(new Span("checking your setup…", Faint));
            return spans;
        }
        foreach (var (key, value, kind) in badges)
        {
            spans.Add(new Span($" {key.ToUpperInvariant()} ", Dim, ChipBg));
            spans.Add(new Span(" ", Text));
            if (kind is Level.Ok or Level.Warn or Level.Error) spans.Add(new Span("● ", LevelColor(kind)));
            spans.Add(new Span(value, kind == Level.Detail ? Faint : Text));
            spans.Add(new Span("   ", Text));
        }
        return spans;
    }

    static List<Span> NextSpans(string text, Level level)
    {
        if (text.Length == 0) return [];
        string bg = level switch
        {
            Level.Ok => GreenBg,
            Level.Warn => AmberBg,
            Level.Error => RedBg,
            _ => AccentBg,
        };
        return [new(" NEXT STEP ", Ink, bg), new("  ", Text), new(text, level is Level.Ok or Level.Warn or Level.Error ? LevelColor(level) : Bright)];
    }

    static List<Span> TabSpans(string label, bool focused, int offset, int width, out int noteStart, out int noteLength)
    {
        string note = offset > 0 ? focused ? "scrolled up · End to follow" : "scrolled up" : "";
        int noteWidth = note.Length == 0 ? 0 : note.Length + 3;
        int dashes = Math.Max(0, width - (label.Length + 2) - 1 - noteWidth);
        noteStart = label.Length + 2 + 1 + dashes;
        noteLength = note.Length == 0 ? 0 : note.Length + 2;

        var spans = new List<Span>
        {
            new($" {label} ", focused ? Ink : Dim, focused ? AccentBg : ChipBg),
            new(" ", Border),
            new(new string('─', dashes), Border),
        };
        if (note.Length > 0)
        {
            spans.Add(new($" {note} ", Amber));
            spans.Add(new("─", Border));
        }
        return spans;
    }

    static Span[] InputSpans(string label, string value, string example, bool prompting, bool command)
    {
        if (!prompting && label.Length == 0 && value.Length == 0)
            return [new Span("working…", Faint)];
        string labelColor = command ? Accent : Text;
        if (value.Length > 0)
            return [new Span(label, labelColor), new Span(value, Bright)];
        if (example.Length > 0)
            return [new Span(label, labelColor), new Span(example, Faint)];
        return [new Span(label, labelColor)];
    }

    static string Card(IReadOnlyList<Span> spans, int inner) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "│" + Text + " "
        + RenderInner(spans, inner)
        + Panel + " " + Border + "│"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string RenderInner(IReadOnlyList<Span> spans, int width)
    {
        var sb = new StringBuilder();
        sb.Append(Panel);
        int used = 0;
        foreach (var span in spans)
        {
            if (used >= width || span.Text.Length == 0) continue;
            string t = span.Text;
            if (used + t.Length > width) t = Clip(t, width - used);
            sb.Append(span.Bg ?? Panel).Append(span.Fg).Append(t);
            used += t.Length;
            if (span.Bg != null) sb.Append(Panel);
        }
        if (used < width) sb.Append(Panel).Append(new string(' ', width - used));
        return sb.ToString();
    }

    static string MarginRow(int w) => Backdrop + new string(' ', w) + Reset;

    static string TopBorder(int cardW) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "╭" + new string('─', cardW - 2) + "╮"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string BottomBorder(int cardW) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "╰" + new string('─', cardW - 2) + "╯"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string Clip(string text, int max)
    {
        if (max <= 0) return "";
        if (text.Length <= max) return text;
        return max <= 1 ? "…" : text[..(max - 1)] + "…";
    }

    static Span GlyphSpan(Level level) => level switch
    {
        Level.Error => new Span("✕", Red),
        Level.Warn => new Span("▲", Amber),
        Level.Ok => new Span("✓", Green),
        Level.Info => new Span("•", Blue),
        Level.Detail => new Span("·", Faint),
        Level.Proxy => new Span("»", Accent),
        _ => new Span(" ", Dim),
    };

    static string LevelColor(Level level) => level switch
    {
        Level.Ok => Green,
        Level.Warn => Amber,
        Level.Error => Red,
        _ => Blue,
    };

    static string TextColor(Level level) => level switch
    {
        Level.Error => ErrorText,
        Level.Warn => WarnText,
        Level.Detail => Faint,
        _ => Text,
    };

    static string Fg(int r, int g, int b) => $"{Esc}[38;2;{r};{g};{b}m";
    static string Bg(int r, int g, int b) => $"{Esc}[48;2;{r};{g};{b}m";

    static int SafeWidth() { try { return Math.Max(56, System.Console.WindowWidth); } catch { return 100; } }
    static int SafeHeight() { try { return Math.Max(18, System.Console.WindowHeight); } catch { return 30; } }
    static int WindowTop() { try { return System.Console.WindowTop; } catch { return 0; } }
    static int WindowLeft() { try { return System.Console.WindowLeft; } catch { return 0; } }

    static void Write(string s)
    {
        lock (draw) { try { System.Console.Out.Write(s); System.Console.Out.Flush(); } catch { } }
    }
}

internal static class Log
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "launcher.log");

    static readonly object Sync = new();
    static bool atPrompt;

    [ThreadStatic] static int essential;

    public readonly struct EssentialScope : IDisposable
    {
        public void Dispose() => essential--;
    }

    public static EssentialScope Essential()
    {
        essential++;
        return new EssentialScope();
    }

    public static void Reset()
    {
        try { File.WriteAllText(FilePath, ""); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Emit(Ui.Level.Info, "[*]", ConsoleColor.Cyan, message);
    public static void Ok(string message) => Emit(Ui.Level.Ok, "[+]", ConsoleColor.Green, message);
    public static void Warn(string message, string? topic = null) => Emit(Ui.Level.Warn, "[!]", ConsoleColor.Yellow, message, topic);
    public static void Error(string message, string? topic = null) => Emit(Ui.Level.Error, "[-]", ConsoleColor.Red, message, topic);
    public static void Detail(string message) => Emit(Ui.Level.Detail, "[.]", ConsoleColor.DarkGray, message);

    public static void Resolve(string topic)
    {
        if (Ui.Active) Ui.Resolve(topic);
    }

    public static void Raw(string text)
    {
        if (Ui.Active)
        {
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) Ui.Add(Ui.Level.Plain, line.TrimEnd('\r'), keep: essential > 0);
            return;
        }
        lock (Sync) System.Console.WriteLine(text);
    }

    public static string? ReadCommand()
    {
        if (Ui.Active)
        {
            string command = Ui.ReadCommand();
            Append("> " + command);
            return command;
        }

        lock (Sync) { System.Console.Write("> "); atPrompt = true; }
        string? typed = System.Console.ReadLine()?.Trim();
        lock (Sync) { atPrompt = false; if (typed != null) Append("> " + typed); }
        return typed;
    }

    public static string? ReadValue(string label, bool secret = false, string example = "", IReadOnlyList<Ui.Button>? buttons = null)
    {
        if (Ui.Active)
        {
            string value = Ui.ReadLine(label, secret, example, buttons);
            if (value.Length > 0) Append(label + (secret ? "***" : value));
            return value.Length == 0 ? null : value;
        }

        lock (Sync) System.Console.Write(label);
        string? typed = System.Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(typed)) lock (Sync) Append(label + (secret ? "***" : typed));
        return string.IsNullOrEmpty(typed) ? null : typed;
    }

    static readonly Ui.Button[] YesNo = [new("Yes", "y"), new("No", "n")];

    public static bool Confirm(string question, bool defaultYes)
    {
        string? answer = ReadValue($"{question} [{(defaultYes ? "Y/n" : "y/N")}] ", buttons: YesNo);
        return answer == null ? defaultYes : answer.ToLowerInvariant() is "y" or "yes";
    }

    static void Emit(Ui.Level level, string tag, ConsoleColor color, string message, string? topic = null)
    {
        Append($"{tag} {message}");
        if (Ui.Active)
        {
            Ui.Add(level, message, topic, keep: essential > 0 || topic != null);
            return;
        }

        lock (Sync)
        {
            if (atPrompt) System.Console.Write("\r  \r");
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write($"{DateTime.Now:HH:mm:ss} ");
            System.Console.ForegroundColor = color;
            System.Console.Write(tag);
            System.Console.ResetColor();
            System.Console.WriteLine($" {message}");
            if (atPrompt) System.Console.Write("> ");
        }
    }

    static void Append(string line)
    {
        try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
