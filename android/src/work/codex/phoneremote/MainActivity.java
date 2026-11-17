package work.codex.phoneremote;

import android.app.Activity;
import android.app.Dialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.io.ByteArrayInputStream;
import java.util.Collections;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.SSLException;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private static final int BACKGROUND = Color.rgb(16, 26, 25);
    private static final int HEADER = Color.rgb(20, 35, 34);
    private static final int ACCENT = Color.rgb(177, 242, 210);
    private static final int MUTED = Color.rgb(180, 199, 193);
    private static final int NOTICE = Color.rgb(255, 207, 170);
    private static final String REMOTE_PAGE = "file:///android_asset/remote.html";
    private final ThreadPoolExecutor requests = new ThreadPoolExecutor(
        2, 2, 15, TimeUnit.SECONDS, new ArrayBlockingQueue<>(8));
    private final Object pairingLock = new Object();
    private ProfileStore store;
    private LinearLayout root;
    private LinearLayout modeBar;
    private ScrollView settingsPage;
    private RemoteProfile.Mode editingMode;
    private TextView remoteFailure;
    private Dialog guide;
    private RemoteProfile profile;
    private volatile WebView web;
    private volatile boolean paused;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        store = new ProfileStore(this);
        profile = store.profile();
        if (profile == null) {
            resetRoot();
            showSettings(RemoteProfile.Mode.LAN);
        } else {
            showRemote(false);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        paused = false;
        if (web != null) web.onResume();
    }

    @Override
    protected void onPause() {
        paused = true;
        if (web != null) web.onPause();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        if (guide != null) guide.dismiss();
        closeWeb();
        requests.shutdownNow();
        super.onDestroy();
    }

    private void showSettings(RemoteProfile.Mode mode) {
        if (settingsPage != null && editingMode == mode) return;
        if (settingsPage != null) root.removeView(settingsPage);
        if (web != null) web.setVisibility(View.GONE);
        if (remoteFailure != null) remoteFailure.setVisibility(View.GONE);

        ScrollView scroll = new ScrollView(this);
        settingsPage = scroll;
        editingMode = mode;
        scroll.setTag("settings");
        scroll.setFillViewport(true);
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setGravity(Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        form.setPadding(dp(20), dp(22), dp(20), dp(24));
        scroll.addView(form);
        form.addView(label("连接设置 · " + mode.label, 23, Color.WHITE), fullWidth());
        String network = mode == RemoteProfile.Mode.LAN ? "手机和电脑应在同一个可信的 Wi-Fi 下。" :
            "手机和电脑应连接到同一个 Tailscale 网络。";
        addSpaced(form, label(network + "从电脑端复制此模式的连接信息，再粘贴到这里。也可以分别填写地址和证书指纹。", 15, MUTED), 12);

        EditText address = field("https://IP:8443");
        EditText fingerprint = field("完整的 SHA-256 指纹");
        RemoteProfile saved = store.profile(mode);
        if (saved != null) {
            address.setText(saved.origin);
            fingerprint.setText(saved.fingerprint);
        }
        TextView message = label("", 14, NOTICE);
        message.setVisibility(View.GONE);
        message.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        Button paste = new Button(this);
        paste.setText("粘贴连接信息");
        paste.setOnClickListener(view -> pasteConnectionInfo(mode, address, fingerprint, message));
        addSpaced(form, paste, 12);
        addSpaced(form, fieldLabel("电脑地址", address), 16);
        addSpaced(form, address, 4);
        addSpaced(form, fieldLabel("证书指纹", fingerprint), 14);
        addSpaced(form, fingerprint, 4);
        addSpaced(form, message, 10);
        Button connect = new Button(this);
        connect.setText("保存并连接");
        connect.setTextColor(ACCENT);
        addSpaced(form, connect, 12);
        connect.setOnClickListener(view -> {
            RemoteProfile updated;
            try {
                updated = RemoteProfile.parse(address.getText().toString(), fingerprint.getText().toString());
                requireMode(mode, updated);
            } catch (IllegalArgumentException error) {
                report(message, error.getMessage(), NOTICE);
                return;
            }
            InputMethodManager keyboard = getSystemService(InputMethodManager.class);
            if (keyboard != null) keyboard.hideSoftInputFromWindow(address.getWindowToken(), 0);
            if (updated.matches(profile) && web != null) {
                hideSettings();
                return;
            }
            boolean switched = profile != null && profile.mode != updated.mode;
            store.saveProfile(updated);
            profile = updated;
            showRemote(switched);
        });

        if (profile != null) {
            Button cancel = new Button(this);
            cancel.setText("返回遥控");
            addSpaced(form, cancel, 4);
            cancel.setOnClickListener(view -> hideSettings());
        }
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        updateModeButtons(mode);
    }

    private void hideSettings() {
        if (settingsPage == null) return;
        root.removeView(settingsPage);
        settingsPage = null;
        editingMode = null;
        if (web != null) web.setVisibility(View.VISIBLE);
        if (remoteFailure != null) remoteFailure.setVisibility(View.VISIBLE);
        updateModeButtons(profile.mode);
    }

    private void selectMode(RemoteProfile.Mode mode) {
        RemoteProfile saved = store.selectProfile(mode);
        if (saved == null) {
            showSettings(mode);
        } else if (saved.matches(profile) && web != null) {
            hideSettings();
        } else {
            boolean switched = profile != null && profile.mode != mode;
            profile = saved;
            showRemote(switched);
        }
    }

    private void updateModeButtons(RemoteProfile.Mode active) {
        modeBar.removeAllViews();
        for (RemoteProfile.Mode mode : RemoteProfile.Mode.values()) {
            boolean configured = store.profile(mode) != null;
            boolean selected = mode == active;
            Button option = new Button(this);
            String title = configured ? mode.label : "设置 " + mode.label;
            option.setText(title);
            option.setAllCaps(false);
            option.setSelected(selected);
            option.setTextColor(selected ? BACKGROUND : Color.WHITE);
            option.setBackgroundTintList(ColorStateList.valueOf(selected ? ACCENT : HEADER));
            option.setContentDescription(selected ? title + "，已选择" : title);
            option.setOnClickListener(view -> selectMode(mode));
            modeBar.addView(option, new LinearLayout.LayoutParams(0, -2, 1));
        }
    }

    private void showRemote(boolean explicitModeSwitch) {
        closeWeb();
        resetRoot();
        updateModeButtons(profile.mode);
        WebView view = new WebView(this);
        web = view;
        view.setBackgroundColor(BACKGROUND);
        root.addView(view, new LinearLayout.LayoutParams(-1, 0, 1));
        WebSettings config = view.getSettings();
        config.setJavaScriptEnabled(true);
        config.setAllowFileAccess(false);
        config.setAllowContentAccess(false);
        config.setBlockNetworkLoads(true);
        config.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        config.setJavaScriptCanOpenWindowsAutomatically(false);
        config.setSupportMultipleWindows(false);
        view.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView source, WebResourceRequest request) {
                return true;
            }

            @Override
            public WebResourceResponse shouldInterceptRequest(WebView source, WebResourceRequest request) {
                return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked", Collections.emptyMap(),
                    new ByteArrayInputStream(new byte[0]));
            }
        });
        try {
            NativeBridge bridge = new NativeBridge(view, new RemoteTransport(profile, store, explicitModeSwitch));
            view.addJavascriptInterface(bridge, "AndroidBridge");
            view.loadUrl(REMOTE_PAGE);
        } catch (Exception error) {
            closeWeb();
            remoteFailure = label("无法开启遥控界面。请重新安装应用。", 16, Color.WHITE);
            addSpaced(root, remoteFailure, 12);
        }
    }

    private static void requireMode(RemoteProfile.Mode mode, RemoteProfile profile) {
        if (mode != profile.mode)
            throw new IllegalArgumentException("当前设置的是" + mode.label + "。请填入此模式的地址和指纹，或点上方按钮切换模式。");
    }

    private void pasteConnectionInfo(RemoteProfile.Mode mode, EditText address, EditText fingerprint, TextView message) {
        ClipboardManager clipboard = getSystemService(ClipboardManager.class);
        ClipData clip;
        try {
            clip = clipboard == null ? null : clipboard.getPrimaryClip();
        } catch (SecurityException error) {
            report(message, "无法读取剪贴板，请手动填写电脑地址和证书指纹。", NOTICE);
            return;
        }
        if (clip == null || clip.getItemCount() != 1 || clip.getItemAt(0).getText() == null) {
            report(message, "剪贴板没有连接信息，请先从电脑端复制地址和证书指纹。", NOTICE);
            return;
        }
        try {
            RemoteProfile pasted = RemoteProfile.parseConnectionInfo(clip.getItemAt(0).getText());
            requireMode(mode, pasted);
            address.setText(pasted.origin);
            fingerprint.setText(pasted.fingerprint);
            report(message, "已填入地址和证书指纹。核对后点击「保存并连接」。", ACCENT);
        } catch (IllegalArgumentException error) {
            report(message, error.getMessage(), NOTICE);
        }
    }

    private void showGuide() {
        if (guide != null) return;
        Dialog page = new Dialog(this);
        page.requestWindowFeature(Window.FEATURE_NO_TITLE);
        guide = page;
        page.setOnDismissListener(dialog -> guide = null);

        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setBackgroundColor(BACKGROUND);
        Button back = new Button(this);
        back.setText("返回");
        back.setOnClickListener(view -> page.dismiss());
        LinearLayout header = titleRow("使用指南", back);
        header.setBackgroundColor(HEADER);
        header.setPadding(dp(20), dp(7), dp(12), dp(7));
        layout.addView(header, fullWidth());

        ScrollView scroll = new ScrollView(this);
        LinearLayout sections = new LinearLayout(this);
        sections.setOrientation(LinearLayout.VERTICAL);
        sections.setPadding(dp(20), 0, dp(20), dp(26));
        scroll.addView(sections, fullWidth());
        layout.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        guideSection(sections, "两种连接方式",
            "同一 Wi-Fi 下，让手机和电脑接入同一个可信的局域网，使用「局域网」。\n\n" +
            "使用不同的 Wi-Fi 或手机流量时，两端都要安装并连接 Tailscale，加入同一个 Tailscale 网络。在手机上按系统提示允许 VPN，再使用「Tailscale」。");
        guideSection(sections, "首次设置与配对",
            "1. 在电脑解压并打开 PhoneRemote.exe。分别选择并启动需要的「局域网」和「Tailscale」。设置完成后，可以选择「后台运行」收起电脑窗口。\n\n" +
            "2. 在电脑点对应模式的「复制连接信息」，把复制的两行文字发到手机并在手机上复制。也可以对照电脑显示的地址和证书指纹手动填写。\n\n" +
            "3. 在手机 App 顶部点「设置 局域网」或「设置 Tailscale」，再点「粘贴连接信息」。核对地址和完整指纹后点「保存并连接」。\n\n" +
            "4. 在电脑为该模式点击「生成新码」，在手机输入显示的 8 位配对码。勾选「记住这部手机，之后直接连接」，再点「连接」。另一种方式可以按同样步骤单独配对。");
        guideSection(sections, "再次连接与切换",
            "App 会分别保存两种方式的地址、证书指纹和配对。打开 App 后会尝试恢复上次使用的方式。要换网络，在顶部点「局域网」或「Tailscale」，App 会连接对应的已配对电脑。\n\n" +
            "连接失败时，可以点「连接已配对的电脑」重试。某种方式的地址或指纹变化时，选中这项再打开「连接设置」更新。若已配对却无法重连，到电脑对应模式点「忘记手机」后重新配对。");
        guideSection(sections, "电脑后台运行与退出",
            "电脑端选择「后台运行」后，窗口收起，已启动的连接继续运行。右下角托盘图标可以查看局域网和 Tailscale 的状态，双击图标可打开操作面板。图标可能收在任务栏的隐藏图标区域。\n\n" +
            "选择「退出应用」会停止两种遥控服务并结束程序，但不会删除电脑和手机已保存的配对。再次使用时，打开电脑端并启动需要的连接，再在手机上选择对应方式。");
        guideSection(sections, "遇到连接问题",
            "连接超时时，确认电脑端已启动，电脑保持开机、联网和桌面可见。同网模式下，访客 Wi-Fi 可能隔离两台设备。跨网模式下，确认两端 Tailscale 均在线，手机能访问电脑的 TCP 8443。也可核对 Windows 防火墙是否允许当前网络访问电脑端程序。\n\n" +
            "证书验证失败时，在电脑旁核对当前地址和完整 SHA-256 指纹，复制后重新粘贴到「连接设置」。\n\n" +
            "配对码仅在生成后的 5 分钟内有效。提示错误或过期时，在电脑端点击「生成新码」，把显示的 8 位数字填到手机的配对页。");

        page.setContentView(layout);
        page.show();
        Window window = page.getWindow();
        if (window != null) {
            window.setBackgroundDrawable(new ColorDrawable(BACKGROUND));
            window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);
            window.setLayout(WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT);
        }
    }

    private void guideSection(LinearLayout sections, String title, String body) {
        TextView heading = label(title, 18, ACCENT);
        heading.setAccessibilityHeading(true);
        addSpaced(sections, heading, 24);
        TextView paragraph = label(body, 16, Color.WHITE);
        paragraph.setLineSpacing(dp(4), 1);
        addSpaced(sections, paragraph, 8);
    }

    private LinearLayout titleRow(String title, Button action) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        TextView heading = label(title, 23, Color.WHITE);
        heading.setAccessibilityHeading(true);
        row.addView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(action);
        return row;
    }

    private TextView fieldLabel(String title, EditText field) {
        TextView caption = label(title, 14, MUTED);
        field.setId(View.generateViewId());
        caption.setLabelFor(field.getId());
        return caption;
    }

    private void report(TextView message, String text, int color) {
        message.setTextColor(color);
        message.setText(text);
        message.setVisibility(View.VISIBLE);
    }

    private void closeWeb() {
        if (web == null) return;
        WebView old = web;
        web = null;
        old.removeJavascriptInterface("AndroidBridge");
        root.removeView(old);
        old.destroy();
    }

    private void resetRoot() {
        settingsPage = null;
        editingMode = null;
        remoteFailure = null;
        root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BACKGROUND);
        setContentView(root);

        LinearLayout toolbar = new LinearLayout(this);
        toolbar.setBackgroundColor(HEADER);
        toolbar.setPadding(dp(12), 0, dp(8), 0);
        toolbar.setGravity(Gravity.CENTER_VERTICAL);
        TextView host = label(profile == null ? "手机遥控电脑" : profile.host, 14, MUTED);
        toolbar.addView(host, new LinearLayout.LayoutParams(0, -2, 1));
        if (profile != null) {
            Button settings = new Button(this);
            settings.setText("连接设置");
            settings.setOnClickListener(view -> showSettings(profile.mode));
            toolbar.addView(settings);
        }
        Button help = new Button(this);
        help.setText("使用指南");
        help.setOnClickListener(view -> showGuide());
        toolbar.addView(help);
        root.addView(toolbar, fullWidth());

        modeBar = new LinearLayout(this);
        modeBar.setPadding(dp(8), 0, dp(8), 0);
        root.addView(modeBar, fullWidth());
    }

    private EditText field(String hint) {
        EditText field = new EditText(this);
        field.setSingleLine(true);
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI |
            InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        field.setHint(hint);
        field.setTextSize(16);
        return field;
    }

    private TextView label(String value, int size, int color) {
        TextView label = new TextView(this);
        label.setText(value);
        label.setTextSize(size);
        label.setTextColor(color);
        return label;
    }

    private void addSpaced(LinearLayout layout, View view, int marginTop) {
        LinearLayout.LayoutParams params = fullWidth();
        params.topMargin = dp(marginTop);
        layout.addView(view, params);
    }

    private LinearLayout.LayoutParams fullWidth() {
        return new LinearLayout.LayoutParams(-1, -2);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    public final class NativeBridge {
        private final WebView target;
        private final RemoteTransport transport;
        private final AtomicBoolean certificateNotice = new AtomicBoolean();

        NativeBridge(WebView target, RemoteTransport transport) {
            this.target = target;
            this.transport = transport;
        }

        @JavascriptInterface
        public void request(int id, String path, String method, String body, String bearer) {
            if (id <= 0 || web != target) return;
            if (!RemoteTransport.allows(path, method) || (paused && !"/api/disconnect".equals(path))) {
                finish(id, new RemoteTransport.Result(0, ""));
                return;
            }
            try {
                requests.execute(() -> {
                    RemoteTransport.Result result;
                    try {
                        if ("/api/pair".equals(path) || "/api/resume".equals(path)) {
                            synchronized (pairingLock) {
                                result = requestIfCurrent(path, method, body, bearer);
                            }
                        } else {
                            result = requestIfCurrent(path, method, body, bearer);
                        }
                    } catch (SSLException error) {
                        if (certificateNotice.compareAndSet(false, true)) {
                            runOnUiThread(() -> {
                                if (web == target) Toast.makeText(MainActivity.this,
                                    "证书验证失败。请核对电脑地址和证书指纹。", Toast.LENGTH_LONG).show();
                            });
                        }
                        result = new RemoteTransport.Result(0, "");
                    } catch (Exception error) {
                        result = new RemoteTransport.Result(0, "");
                    }
                    finish(id, result);
                });
            } catch (RejectedExecutionException error) {
                finish(id, new RemoteTransport.Result(0, ""));
            }
        }

        private RemoteTransport.Result requestIfCurrent(String path, String method, String body, String bearer)
            throws Exception {
            if (web != target || (paused && !"/api/disconnect".equals(path)))
                return new RemoteTransport.Result(0, "");
            return transport.request(path, method, body, bearer);
        }

        private void finish(int id, RemoteTransport.Result result) {
            runOnUiThread(() -> {
                if (web != target) return;
                if (result.rememberFailed) Toast.makeText(MainActivity.this,
                    "手机未能保存配对。请在电脑端点击「忘记手机」，再重新配对。", Toast.LENGTH_LONG).show();
                String data = JSONObject.quote(result.payload);
                target.evaluateJavascript("window.finishAndroidRequest(" +
                    id + "," + result.status + "," + data + ")", null);
            });
        }
    }
}
