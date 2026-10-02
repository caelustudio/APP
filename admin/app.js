// 作品管理台 · 交互逻辑
// 依赖：/admin/app.js 之前必须加载 WorkBuddyCloud SDK（见 index.html）

// 来自云服务的 publicConfig（由 workbuddy_cloud_service 激活时返回）
var publicConfig = {
  endpoint: "https://caelus-admin.app.workbuddy.host",
  oauthRelayBaseUrl: "https://www.workbuddy.cn/v2/as/genie-baas/oauth",
  publishableKey: "wbpk_BSMrDJegwuwubsF6JsY7Rg_ofwiNwo48RcfzCgn04INPBbAiFepfWOd"
};

// 云端按域名精确校验 Origin，只有这一个域名能正常读写
var OFFICIAL_HOST = "caelus-admin.app.workbuddy.host";

var cloud = WorkBuddyCloud.createWorkBuddyCloud({
  endpoint: publicConfig.endpoint,
  oauthRelayBaseUrl: publicConfig.oauthRelayBaseUrl,
  publishableKey: publicConfig.publishableKey
});

var FIELDS = [
  "treeos_enabled", "treeos_version", "treeos_url", "treeos_note",
  "caelusos_enabled", "caelusos_version", "caelusos_url", "caelusos_note",
  "app_version", "app_url", "app_notes", "app_force"
];
var TOGGLES = ["treeos_enabled", "caelusos_enabled", "app_force"];
var pendingOtp = null;
var currentUser = null;

function $(id) { return document.getElementById(id); }

function setMsg(el, text, kind) {
  el.className = "msg" + (kind ? " " + kind : "");
  el.innerHTML = text || "";
}

// ---------- 读写配置 ----------

function readForm() {
  var out = {};
  FIELDS.forEach(function (f) {
    var el = $(f);
    out[f] = TOGGLES.indexOf(f) >= 0 ? el.checked : el.value.trim();
  });
  return out;
}

function writeForm(cfg) {
  FIELDS.forEach(function (f) {
    var el = $(f);
    if (TOGGLES.indexOf(f) >= 0) el.checked = cfg[f] === true;
    else el.value = cfg[f] == null ? "" : String(cfg[f]);
  });
  $("updatedAt").textContent = cfg.updated_at
    ? new Date(cfg.updated_at).toLocaleString("zh-CN") + (cfg.updated_by ? " · " + cfg.updated_by : "")
    : "—";
}

function setEditable(on) {
  FIELDS.forEach(function (f) { $(f).disabled = !on; });
  $("btnSave").disabled = !on;
  $("dirtyTip").classList.toggle("hidden", on);
}

async function loadConfig() {
  setMsg($("saveMsg"), '<span class="spinner"></span> 读取中…');
  var res = await cloud.database.from("app_config").select("*").eq("id", 1).maybeSingle();
  if (res.error) {
    setMsg($("saveMsg"), "读取失败：" + res.error.message, "err");
    return;
  }
  if (!res.data) {
    setMsg($("saveMsg"), "配置尚未初始化。", "err");
    return;
  }
  writeForm(res.data);
  setMsg($("saveMsg"), "", "");
}

async function saveConfig() {
  var session = await cloud.auth.getSession();
  if (session.error || !session.data) {
    setMsg($("saveMsg"), "请先登录再保存。", "err");
    return;
  }
  setMsg($("saveMsg"), '<span class="spinner"></span> 保存中…');
  var payload = readForm();
  payload.updated_at = new Date().toISOString();
  payload.updated_by = currentUser || "";

  var res = await cloud.database.from("app_config").update(payload).eq("id", 1).select();
  if (res.error) {
    setMsg($("saveMsg"), "保存失败：" + res.error.message, "err");
    return;
  }
  // RLS 拦截时返回空数组而不是报错
  if (!res.data || res.data.length === 0) {
    setMsg($("saveMsg"), "没有保存成功：当前账号没有修改权限。", "err");
    return;
  }
  writeForm(res.data[0]);
  setMsg($("saveMsg"), "已保存，客户端将读取到最新配置。", "ok");
}

// ---------- 登录 ----------

async function refreshSession() {
  var s = await cloud.auth.getSession();
  var ok = !s.error && !!s.data;
  currentUser = ok ? (s.data.user && (s.data.user.id || s.data.user.email) || "已登录") : null;
  $("authCard").classList.toggle("hidden", ok);
  setEditable(ok);
  $("chip").innerHTML = ok
    ? '<span style="color:var(--ok)">●</span> ' + (currentUser || "") + ' <button class="link" id="btnOut">退出</button>'
    : "";
  var out = $("btnOut");
  if (out) out.onclick = async function () {
    await cloud.auth.signOut();
    location.reload();
  };
  return ok;
}

async function doLogin() {
  var email = $("liEmail").value.trim();
  var password = $("liPwd").value;
  if (!email || !password) { setMsg($("liMsg"), "请填写邮箱和密码。", "err"); return; }
  setMsg($("liMsg"), '<span class="spinner"></span> 登录中…');
  var r = await cloud.auth.signInWithPassword({ email: email, password: password });
  if (r.error) { setMsg($("liMsg"), "邮箱或密码不正确。", "err"); return; }
  setMsg($("liMsg"), "", "");
  await refreshSession();
}

async function sendCode() {
  var email = $("rgEmail").value.trim();
  if (!email) { setMsg($("rgMsg"), "请先填写邮箱。", "err"); return; }
  setMsg($("rgMsg"), '<span class="spinner"></span> 发送中…');
  var sent = await cloud.auth.sendOtp({ email: email });
  if (sent.error) { setMsg($("rgMsg"), "发送失败：" + sent.error.message, "err"); return; }
  pendingOtp = { email: email, verificationId: sent.data.verificationId, isExistingUser: sent.data.isExistingUser };
  setMsg($("rgMsg"), "验证码已发送，请查收邮件。", "ok");
}

async function doRegister() {
  var email = $("rgEmail").value.trim();
  var password = $("rgPwd").value;
  var token = $("rgCode").value.trim();
  if (!email || !password || !token) { setMsg($("rgMsg"), "请填写邮箱、密码和验证码。", "err"); return; }
  if (password.length < 6) { setMsg($("rgMsg"), "密码至少 6 位。", "err"); return; }
  if (!pendingOtp || pendingOtp.email !== email) { setMsg($("rgMsg"), "请先为当前邮箱获取验证码。", "err"); return; }
  setMsg($("rgMsg"), '<span class="spinner"></span> 验证中…');
  var done = await cloud.auth.verifyOtp({
    email: pendingOtp.email,
    verificationId: pendingOtp.verificationId,
    isExistingUser: pendingOtp.isExistingUser,
    token: token,
    password: pendingOtp.isExistingUser ? undefined : password
  });
  if (done.error) { setMsg($("rgMsg"), "验证失败：" + done.error.message, "err"); return; }
  pendingOtp = null;
  setMsg($("rgMsg"), "", "");
  await refreshSession();
}

// ---------- 非授权域名提示 ----------

// 云端按域名精确校验 Origin：非授权域名（比如 GitHub Pages 上的副本）
// 连读配置都会被拒（403 access_denied）。先自检，避免打开副本时只剩莫名报错。
function showMirrorNotice() {
  var bar = document.createElement("div");
  bar.className = "banner";
  bar.style.borderColor = "#5A3A1C";
  bar.innerHTML =
    '<span class="dot" style="background:#FFB020"></span>' +
    '<div>这是管理页的<b>只读副本</b>（当前域名 <code>' + location.hostname + '</code>' +
    ' 未被云端授权）。云端按域名精确校验，这里无法读取或保存配置。' +
    '请使用正式地址 <a href="https://caelus-admin.app.workbuddy.host/" ' +
    'style="color:var(--accent)">caelus-admin.app.workbuddy.host</a>。</div>';
  var wrap = document.querySelector(".wrap");
  wrap.insertBefore(bar, wrap.children[1]);
}

// ---------- 绑定 ----------

$("ep").textContent = publicConfig.endpoint + "/.cloud/database/rest/app_config?select=*";
$("btnSave").onclick = saveConfig;
$("btnReload").onclick = loadConfig;
$("btnLogin").onclick = doLogin;
$("btnSend").onclick = sendCode;
$("btnReg").onclick = doRegister;
$("liPwd").addEventListener("keydown", function (e) { if (e.key === "Enter") doLogin(); });
$("rgCode").addEventListener("keydown", function (e) { if (e.key === "Enter") doRegister(); });

$("tabLogin").onclick = function () {
  $("tabLogin").classList.add("active"); $("tabReg").classList.remove("active");
  $("paneLogin").classList.remove("hidden"); $("paneReg").classList.add("hidden");
};
$("tabReg").onclick = function () {
  $("tabReg").classList.add("active"); $("tabLogin").classList.remove("active");
  $("paneReg").classList.remove("hidden"); $("paneLogin").classList.add("hidden");
};

FIELDS.forEach(function (f) {
  $(f).addEventListener("change", function () {
    $("dirtyTip").classList.add("hidden");
  });
});

(async function init() {
  setEditable(false);
  if (location.hostname !== OFFICIAL_HOST) {
    showMirrorNotice();
    setMsg($("saveMsg"), "当前域名未被授权，已停止读取云端配置。", "err");
    return;
  }
  await loadConfig();
  await refreshSession();
})();
