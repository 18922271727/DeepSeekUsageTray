using System;
using System.Windows.Forms;

namespace DeepSeekUsageTray;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppConfig _config = ConfigStore.Load();
    private readonly NotifyIcon _tray;
    private readonly MainForm _main;
    private SettingsForm? _settings;
    private LoginForm? _login;
    private BiliPublishForm? _biliPublish;

    public TrayApp()
    {
        _main = new MainForm(_config)
        {
            OnOpenSettings = OpenSettings,
            OnNeedLogin = OpenLogin
        };

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(),
            Text = "DeepSeek 用量",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleWindow());
        menu.Items.Add("立即刷新", null, (_, _) => _main.RefreshNow());
        menu.Items.Add("扫码登录 DeepSeek…", null, (_, _) => OpenLogin());
        menu.Items.Add("B站发帖器…", null, (_, _) => OpenBiliPublish());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleWindow();
        _main.Show();
    }

    private void ToggleWindow()
    {
        if (_main.Visible)
        {
            _main.Hide();
        }
        else
        {
            _main.Show();
            _main.Activate();
        }
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsForm(_config);
        _settings.FormClosed += (_, _) =>
        {
            if (_settings?.Saved == true)
            {
                _main.RefreshNow();
            }
            _settings = null;
        };
        _settings.ShowDialog(_main.Visible ? _main : null);
    }

    private void OpenLogin()
    {
        if (_login is { IsDisposed: false })
        {
            _login.Activate();
            return;
        }

        _login = new LoginForm(_config);
        _login.FormClosed += (_, _) =>
        {
            if (_login?.GotToken == true)
            {
                _main.RefreshNow();
            }
            _login = null;
        };
        _login.ShowDialog(_main.Visible ? _main : null);
    }

    private void OpenBiliPublish()
    {
        if (_biliPublish is { IsDisposed: false })
        {
            _biliPublish.Activate();
            return;
        }

        _biliPublish = new BiliPublishForm();
        _biliPublish.FormClosed += (_, _) => _biliPublish = null;
        _biliPublish.Show(_main.Visible ? _main : null);
    }

    private void ExitApp()
    {
        _main.SavePosition();
        ConfigStore.Save(_config);
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}
