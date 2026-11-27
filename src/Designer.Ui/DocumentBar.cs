using Brutal.ImGuiApi;

namespace BrutalGame.Designer.Ui;

/// <summary>What the Add menu (a right-click in the view) makes.</summary>
public enum AddKind { None, Box, Wedge, Cylinder, Hallway, Window, Decal, Stroke, Component, Door, DockingPort, Rotor, Hinge, Slider, Opening, PortFace, Chamber, Nozzle, Injector, Igniter, Pipe }

/// <summary>One open plan as the tab bar shows it. <see cref="Key"/> stays the same while the tab lives.</summary>
public readonly record struct DocumentTab(int Key, string Title, bool Modified, bool Component);

/// <summary>
/// The bar across the top: the File menu, the interior view and a tab per open ship or
/// component. Closing a tab with unsaved changes asks first. The host owns the documents; the bar only reports what the
/// user picked this frame.
/// </summary>
public sealed class DocumentBar
{
    private const string ConfirmPopup = "Close without saving?";

    private readonly List<DocumentTab> _tabs = new();
    private int _forceKey = -1;
    private int _confirmKey = -1;
    private bool _openConfirm;

    /// <summary>The interior view: the hull see-through and locked, so what is inside can be reached and edited.</summary>
    public bool InteriorView;

    /// <summary>Preview the rotors and hinges moving (P).</summary>
    public bool PlayJoints;

    /// <summary>The open tab is a component.</summary>
    public bool ComponentTab;

    /// <summary>The File menu's pick this frame.</summary>
    public FileRequest Request { get; private set; }

    /// <summary>The tab the user switched to this frame, or -1.</summary>
    public int SelectedKey { get; private set; } = -1;

    /// <summary>The tab to close this frame (confirmed if it had changes), or -1.</summary>
    public int CloseKey { get; private set; } = -1;

    /// <summary>Brings a tab to the front (a new or reopened document).</summary>
    public void Show(int key) => _forceKey = key;

    /// <summary>Closes a tab, asking first when it has unsaved changes.</summary>
    public void AskClose(int key)
    {
        var tab = _tabs.Find(t => t.Key == key);

        if (tab.Modified)
        {
            _confirmKey = key;
            _openConfirm = true;
        }
        else
        {
            CloseKey = key;
        }
    }

    public void Draw(IReadOnlyList<DocumentTab> tabs, int activeKey)
    {
        Request = FileRequest.None;
        SelectedKey = -1;
        CloseKey = -1;
        _tabs.Clear();
        _tabs.AddRange(tabs);

        if (_forceKey >= 0 && !_tabs.Exists(t => t.Key == _forceKey))
            _forceKey = -1;

        if (ImGui.BeginMainMenuBar())
        {
            DrawFileMenu(activeKey);
            ImGui.Separator();

            if (ImGui.MenuItem("Interior view"u8, "I"u8, InteriorView))
                InteriorView = !InteriorView;

            if (ImGui.MenuItem("Play joints"u8, "P"u8, PlayJoints))
                PlayJoints = !PlayJoints;

            ImGui.Separator();
            DrawTabs(activeKey);
            ImGui.EndMainMenuBar();
        }

        DrawConfirm();
    }

    private void DrawFileMenu(int activeKey)
    {
        if (!ImGui.BeginMenu("File"u8))
            return;

        if (ImGui.MenuItem("New ship"u8, "Ctrl+N"u8))
            Request = FileRequest.New;

        if (ImGui.MenuItem("New component"u8))
            Request = FileRequest.NewComponent;

        ImGui.Separator();

        if (ImGui.MenuItem("Open..."u8, "Ctrl+O"u8))
            Request = FileRequest.Open;

        if (ImGui.MenuItem("Save"u8, "Ctrl+S"u8))
            Request = FileRequest.Save;

        if (ImGui.MenuItem("Save as..."u8))
            Request = FileRequest.SaveAs;

        ImGui.Separator();

        if (ImGui.MenuItem("Close tab"u8, "Ctrl+W"u8))
            AskClose(activeKey);

        ImGui.EndMenu();
    }

    private void DrawTabs(int activeKey)
    {
        if (!ImGui.BeginTabBar("##documents"u8, ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
            return;

        foreach (var tab in _tabs)
        {
            var open = true;
            var flags = tab.Modified ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;

            if (tab.Key == _forceKey)
                flags |= ImGuiTabItemFlags.SetSelected;

            // ### keeps the tab's id fixed while its title changes (saving renames it).
            if (ImGui.BeginTabItem($"{(tab.Component ? "Component: " : "")}{tab.Title}###doc{tab.Key}", ref open, flags))
            {
                // A forced tab takes a frame to come to the front: until it has, the old front tab is not a pick.
                if (_forceKey < 0 && tab.Key != activeKey)
                    SelectedKey = tab.Key;
                else if (tab.Key == _forceKey)
                    _forceKey = -1;

                ImGui.EndTabItem();
            }

            if (!open)
                AskClose(tab.Key);
        }

        ImGui.EndTabBar();
    }

    private void DrawConfirm()
    {
        if (_openConfirm)
        {
            ImGui.OpenPopup(ConfirmPopup);
            _openConfirm = false;
        }

        if (!ImGui.BeginPopupModal(ConfirmPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var tab = _tabs.Find(t => t.Key == _confirmKey);
        ImGui.Text($"{tab.Title} has changes that are not saved.");

        if (ImGui.Button("Close without saving"u8))
        {
            CloseKey = _confirmKey;
            _confirmKey = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel"u8))
        {
            _confirmKey = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
