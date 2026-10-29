using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clothesline.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Clothesline.Windows;
internal sealed partial class RopeWindow
{
    private bool checking;
    private MenuItem Item(string title, Action action, bool check=false)
    {
        var item=new MenuItem { Header=title, IsCheckable=check };
        item.Click += (_,_) => Guard(action); return item;
    }
    private ContextMenu CardMenu(FileRecord file)
    {
        var menu=new ContextMenu(); var temporary=clipboardItems.ContainsKey(file.Path);
        menu.Items.Add(Item(T("打开 / 预览","Open / Preview"),()=>Open(file)));
        // Images get an explicit "copy image" entry: pasting a bitmap into a chat and pasting a file
        // into a folder are different things and both are useful.
        if (IsImage(file.Path))
            menu.Items.Add(Item(T("复制图片","Copy Image"),()=> { CopyToClipboard(file); FlashCard(file.Path,T("已复制","Copied")); }));
        menu.Items.Add(Item(T("复制","Copy"),()=> { CopyToClipboard(file); FlashCard(file.Path,T("已复制","Copied")); }));
        var noteEntry = clipboardItems.TryGetValue(file.Path, out var ne) ? ne : null;
        if (temporary || IsText(file.Path))
        {
            menu.Items.Add(Item(T("复制文字","Copy Text"),()=>CopyText(file)));
            if (noteEntry is not null)
                menu.Items.Add(Item(T("查看全文","View Full Text"),()=>ShowTextNote(file)));
        }
        if (IsImage(file.Path))
            menu.Items.Add(AsyncItem(T("提取文字（OCR）","Extract Text (OCR)"),()=>ExtractText(file)));
        var isRtfNote = noteEntry is not null && noteEntry.IsRtf;
        if (!isRtfNote)
            menu.Items.Add(Item(T("编辑…","Edit…"),()=> {
                Validate(file);
                if (!Native.Edit(file.Path)) throw new IOException(T("找不到可用的编辑程序。","No editor could be started."));
            }));
        if (noteEntry is not null)
            menu.Items.Add(Item(IsFavorite(file.Path) ? T("取消收藏","Unfavorite") : T("收藏到文字信封","Add to Text Envelope"),()=>ToggleFavorite(file.Path)));
        menu.Items.Add(Item(T("另存为…","Save As…"),()=>SaveAs(file)));
        if (!temporary)
        {
            menu.Items.Add(Item(T("在资源管理器中显示","Show in Explorer"),()=> { Validate(file); Native.Reveal(file.Path); }));
            menu.Items.Add(Item(T("重命名…","Rename…"),()=> {
                Validate(file); var stem=file.IsDirectory ? file.Name : System.IO.Path.GetFileNameWithoutExtension(file.Path);
                var chosen=Prompt(T("重命名（保留扩展名）","Rename (keep extension)"),stem,false); if (chosen is null) return;
                var destination=RopeState.Rename(file,chosen);
                var pinned=state.Pinned.Remove(file.Path); state.Dismissed.Remove(file.Path); monitored.Remove(file.Path);
                settings.Attached.RemoveAll(p=>string.Equals(p,file.Path,StringComparison.OrdinalIgnoreCase));
                settings.Recent.RemoveAll(p=>string.Equals(p,file.Path,StringComparison.OrdinalIgnoreCase));
                if (pinned) state.Pinned.Add(destination); Attach([destination]);
            }));
            var pin=Item(T("固定","Pin"),()=> { if (!state.Pinned.Remove(file.Path)) state.Pinned.Add(file.Path); Persist(); },true);
            pin.IsChecked=state.Pinned.Contains(file.Path); menu.Items.Add(pin);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(T("取下（保留原文件）","Take Down (Keep Original)"),()=> { state.Dismiss(file); Persist(); }));
        menu.Items.Add(Item(T("取下（删除原文件）","Take Down (Delete Original)"),()=>DeleteOriginal(file)));
        return menu;
    }

    /// The one action on the menu that destroys something, so it asks first and says plainly where
    /// the file ends up. It goes to the Recycle Bin rather than being erased: the same destination
    /// Explorer's own Delete uses, and the only version of this the user can undo.
    private void DeleteOriginal(FileRecord file)
    {
        Validate(file);
        var prompt = T("把「" + file.Name + "」移入回收站？\n\n卡片会从晾衣绳上取下，原文件不再留在原位置，需要时可从回收站恢复。",
            "Move \"" + file.Name + "\" to the Recycle Bin?\n\nThe card is taken down and the file leaves its location. It can be restored from the Recycle Bin.");
        if (MessageBox.Show(prompt, "Clothesline", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!Native.Recycle(file.Path))
            throw new IOException(T("无法删除原文件，卡片保留在晾衣绳上。","The original could not be deleted, so the card stays on the clothesline."));
        // Dropping it from every tracked list as well, so nothing can bring a deleted path back
        // after the next scan or a restart.
        state.Dismiss(file); settings.Attached.RemoveAll(p=>string.Equals(p,file.Path,StringComparison.OrdinalIgnoreCase));
        settings.Recent.RemoveAll(p=>string.Equals(p,file.Path,StringComparison.OrdinalIgnoreCase));
        monitored.Remove(file.Path); clipboardItems.Remove(file.Path);
        Persist(); signature=""; Render();
        Notice(T("已删除原文件","Original deleted"), T("文件已移入回收站，可以从回收站恢复。","The file was moved to the Recycle Bin and can be restored from there."));
    }
    private void CopyText(FileRecord file)
    {
        Validate(file);
        if (file.IsDirectory) throw new IOException(T("文件夹没有可复制的文字。","A folder has no text to copy."));
        var text = clipboardItems.TryGetValue(file.Path, out var note) && note is not null ? note.Plain : File.ReadAllText(file.Path);
        if (text.Length > 2000000) throw new IOException(T("文字过长，无法复制到剪贴板。","The text is too long to copy."));
        var data=new DataObject(); data.SetText(text);
        data.SetData(OwnCopy,new MemoryStream(new byte[] { 1 }));
        Clipboard.SetDataObject(data,true); clipboardSequence=Native.GetClipboardSequenceNumber();
        FlashCard(file.Path,T("已复制","Copied"));
    }
    private void SaveAs(FileRecord file)
    {
        Validate(file);
        if (file.IsDirectory) throw new IOException(T("文件夹不能另存为文件。","A folder cannot be saved as a single file."));
        var isNote = clipboardItems.TryGetValue(file.Path, out var note) && note is not null;
        var name = isNote ? "Note.txt" : clipboardItems.ContainsKey(file.Path) ? "Clipboard.png" : file.Name;
        var picker=new SaveFileDialog { FileName=name, AddExtension=true };
        if (picker.ShowDialog(this)!=true) return;
        File.Copy(file.Path,picker.FileName,true);
    }
    private string? Prompt(string title,string value,bool multiline)
    {
        var window=new Window { Title=title, Width=420, Height=multiline ? 340 : 160, WindowStartupLocation=WindowStartupLocation.CenterScreen, ResizeMode=ResizeMode.NoResize, Owner=this };
        var panel=new DockPanel { Margin=new Thickness(14) };
        var buttons=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,12,0,0) };
        var ok=new Button { Content=T("保存","Save"), IsDefault=true, Padding=new Thickness(16,4,16,4) };
        var cancel=new Button { Content=T("取消","Cancel"), IsCancel=true, Margin=new Thickness(8,0,0,0), Padding=new Thickness(16,4,16,4) };
        ok.Click += (_,_)=>window.DialogResult=true; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(buttons);
        var field=new TextBox { Text=value, AcceptsReturn=multiline, TextWrapping=multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        panel.Children.Add(field); window.Content=panel; window.Loaded+=(_,_)=> { field.Focus(); if (!multiline) field.SelectAll(); };
        var wasBusy=busy; busy=true; try { return window.ShowDialog()==true ? field.Text : null; } finally { busy=wasBusy; }
    }
    private void EditNote(string path,string text)
    {
        var value=Prompt(T("文字便签","Text Note"),text,true); if (value is null) return;
        if (string.IsNullOrWhiteSpace(value) || System.Text.Encoding.UTF8.GetByteCount(value)>200000) throw new IOException(T("便签不能为空，且需在 200 KB 以内。","Note must not be empty or exceed 200 KB."));
        File.WriteAllText(path,value); clipboardItems[path]=new NoteEntry { Plain=value, IsRtf=false }; signature=""; Render();
    }
    private Forms.ToolStripMenuItem TrayItem(string title,Action action,bool? check=null)
    {
        var item=new Forms.ToolStripMenuItem(title); if (check.HasValue) item.Checked=check.Value;
        item.Click+=(_,_)=>Guard(()=> { action(); signature=""; Render(); }); return item;
    }
    private void BuildTray(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear(); menu.Items.Add(new Forms.ToolStripMenuItem("Clothesline · Windows") { Enabled=false });
        // Everything that is a setting — clip-board hanging, the text envelope, appearance, OCR engine
        // and model, items-to-keep, launch-at-login, language — now lives in the Settings window.
        // The tray keeps only the quick actions that are awkward to reach from there.
        var folders=new Forms.ToolStripMenuItem(T("监控文件夹","Watched Folders")+T("（"," (")+settings.Folders.Count+T("）"," )"));
        folders.DropDownItems.Add(TrayItem(T("管理监控文件夹…","Manage Watched Folders…"),()=>ShowSettings(SettingsPage.Folders)));
        foreach (var folder in settings.Folders)
            folders.DropDownItems.Add(new Forms.ToolStripMenuItem(System.IO.Path.GetFileName(folder)) { Enabled=false,ToolTipText=folder });
        menu.Items.Add(folders);
        if (!string.IsNullOrEmpty(failure)) menu.Items.Add(new Forms.ToolStripMenuItem("⚠ "+failure) { Enabled=false });
        if (!string.IsNullOrEmpty(hotKeyNotice)) menu.Items.Add(new Forms.ToolStripMenuItem("⚠ "+hotKeyNotice) { Enabled=false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem(T("设置…","Settings…"),()=>ShowSettings(SettingsPage.General)));
        var hang=new Forms.ToolStripMenuItem(T("挂上…","Hang…"));
        hang.DropDownItems.Add(TrayItem(T("挂上文件…","Hang Files…"),()=> { var picker=new OpenFileDialog { Multiselect=true }; if (picker.ShowDialog(this)==true) Attach(picker.FileNames); }));
        hang.DropDownItems.Add(TrayItem(T("挂上文件夹…","Hang Folder…"),()=> { var picker=new OpenFolderDialog(); if (picker.ShowDialog(this)==true) Attach([picker.FolderName]); }));
        menu.Items.Add(hang);
        menu.Items.Add(TrayItem(T("最近文件…","Recent Files…"),ShowRecent));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem(T("清空晾衣绳（不删除文件）","Clear Clothesline (Keep Files)"),()=> { foreach (var file in Records()) state.Dismiss(file); Persist(); }));
        menu.Items.Add(TrayItem(T("重新显示已取下的文件","Show Removed Items Again"),()=> { state.Dismissed.Clear(); Persist(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var updates=TrayItem(checking ? T("正在检查更新…","Checking for Updates…") : T("检查更新…","Check for Updates…"),()=>_ = CheckUpdates()); updates.Enabled=!checking; menu.Items.Add(updates);
        menu.Items.Add(TrayItem(T("关于 Clothesline…","About Clothesline…"),ShowAbout));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(TrayItem(T("退出 Clothesline","Quit Clothesline"),Close));
    }
    private void ShowAppearance()
    {
        var window = new AppearanceWindow(LineTheme.FromId(settings.Appearance), theme => {
            settings.Appearance = theme.Id; Persist(); signature = ""; Render();
        });
        window.Owner = this;
        window.ShowDialog();
    }
    private void ShowRecent()
    {
        var window=new Window { Title=T("最近文件","Recent Files"), Width=600, Height=480, Owner=this, WindowStartupLocation=WindowStartupLocation.CenterScreen };
        var panel=new DockPanel { Margin=new Thickness(15) }; var search=new TextBox { Margin=new Thickness(0,0,0,10), ToolTip=T("搜索名称","Search by name") };
        DockPanel.SetDock(search,Dock.Top); panel.Children.Add(search); var list=new ListBox(); panel.Children.Add(list); window.Content=panel;
        void Populate()
        {
            list.Items.Clear();
            foreach (var path in settings.Recent.Where(p=>System.IO.Path.GetFileName(p).Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
            {
                var file=FileRecord.Read(path); if (file is null) continue;
                var row=new DockPanel { Width=520, ToolTip=file.Path }; var hang=new Button { Content=T("挂上","Hang"),Padding=new Thickness(10,3,10,3) };
                hang.Click+=(_,_)=>Guard(()=>Attach([path])); DockPanel.SetDock(hang,Dock.Right); row.Children.Add(hang);
                row.Children.Add(new TextBlock { Text=file.Name, TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(4) }); list.Items.Add(row);
                row.MouseLeftButtonDown+=(_,e)=> { if (e.ClickCount==2) Guard(()=>Native.Open(file.Path)); };
                Point down=new(); row.PreviewMouseLeftButtonDown+=(_,e)=>down=e.GetPosition(row);
                row.MouseMove+=(_,e)=> { if (e.LeftButton==System.Windows.Input.MouseButtonState.Pressed && (e.GetPosition(row)-down).Length>6) DragDrop.DoDragDrop(row,FilePayload(path),DragDropEffects.Copy); };
            }
        }
        search.TextChanged+=(_,_)=>Populate(); Populate(); window.Show();
    }
    private void ShowAbout()
    {
        var license=System.IO.Path.Combine(AppContext.BaseDirectory,"LICENSE.txt");
        var text="Clothesline Windows 0.1.0-rc.11\n\n"+T("把常用文件挂在屏幕顶部，随手预览、拖拽和使用。","Keep files at the top of your screen for quick access and drag and drop.")+"\n\nhttps://github.com/zhongguozhang/Clothesline\n\n"+(File.Exists(license) ? File.ReadAllText(license) : "MIT License — see repository LICENSE");
        var window=new Window { Title=T("关于 Clothesline","About Clothesline"),Width=580,Height=500,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterScreen };
        window.Content=new TextBox { Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(16) }; window.Show();
    }
    private async Task CheckUpdates()
    {
        if (checking) return; checking=true;
        try
        {
            using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(20) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("Clothesline-Windows/0.1.0");
            using var json=JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/zhongguozhang/Clothesline/releases?per_page=100"));
            // Separate platform tag prevents either app from offering the other platform's installer.
            var current=ReleaseVersion.Parse("windows-v0.1.0-rc.11")!;
            var newer=json.RootElement.EnumerateArray().Where(r=>!r.GetProperty("draft").GetBoolean())
                .Select(r=>(Version:ReleaseVersion.Parse(r.GetProperty("tag_name").GetString()),Url:r.GetProperty("html_url").GetString()!))
                .Where(r=>r.Version is not null && r.Version.CompareTo(current)>0 && r.Url.StartsWith("https://github.com/zhongguozhang/Clothesline/releases/tag/windows-v",StringComparison.Ordinal))
                .OrderByDescending(r=>r.Version).FirstOrDefault();
            if (newer.Version is null) MessageBox.Show(T("暂无更新版本。","No newer version available."),"Clothesline");
            else if (MessageBox.Show(T("发现 Windows 新版本，前往下载？","A new Windows version is available. Open the download page?"),"Clothesline",MessageBoxButton.YesNo)==MessageBoxResult.Yes) Native.Open(newer.Url);
        }
        catch (Exception e) { MessageBox.Show(T("无法检查更新，请检查网络后重试。","Unable to check for updates. Check your connection and try again.")+"\n"+e.Message,"Clothesline"); }
        finally { checking=false; }
    }
}
