const KEY = 'kaungmcp.adminKey';
let adminKey = sessionStorage.getItem(KEY) || '';
let tab = 'overview', clients = [], selectedClient = '', clientPage = 1, logPage = 1;
let logSource = '', logToolName = '', toolCatalog = null, toolSearch = '', toolStatus = 'all', toolCategory = '';
let selectedToolName = '', toolTestController = null, toolRawMode = false;
let settings = null, dirty = new Set();
const $ = selector => document.querySelector(selector);
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const date = value => value ? new Date(value).toLocaleString('zh-CN') : '—';
const show = (selector, visible) => $(selector).classList.toggle('hidden', !visible);
function flash(message, error = false) { const box = $('#flash'); box.textContent = message; box.className = `flash${error ? ' error' : ''}`; window.scrollTo({top:0,behavior:'smooth'}); }
function authScreen() { closeLogDetail(); closeToolDetail(); show('#login', true); show('#shell', false); $('#admin-key').value = ''; }
async function api(path, options = {}) {
  const headers = {'X-Kaung-Admin-Key':adminKey, ...options.headers};
  if (options.body && !(options.body instanceof FormData)) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, {...options, headers, cache:'no-store'});
  if (response.status === 401) { adminKey = ''; sessionStorage.removeItem(KEY); authScreen(); throw new Error('管理员密钥无效或已更换。'); }
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    throw new Error(data.detail || data.title || `请求失败：HTTP ${response.status}`);
  }
  return response.status === 204 ? null : response;
}
async function json(path, options) { return (await api(path, options)).json(); }
async function run(task) { try { await task(); } catch (error) { flash(error.message, true); } }
function switchTab(name) {
  tab = name;
  document.querySelectorAll('.view').forEach(view => view.classList.toggle('hidden', view.id !== name));
  document.querySelectorAll('[data-tab]').forEach(button => button.classList.toggle('active', button.dataset.tab === name));
  $('#flash').classList.add('hidden');
  if (name === 'clients') run(loadClients);
  if (name === 'tools') run(loadTools);
  if (name === 'scheduler') run(loadScheduler);
  if (name === 'logs') run(loadLogs);
  if (['search','fetch','service'].includes(name)) run(loadSettings);
  if (name === 'overview') run(loadOverview);
}
async function enter(key) {
  adminKey = key;
  await json('/api/admin/clients?pageSize=1');
  sessionStorage.setItem(KEY, key);
  show('#login', false); show('#shell', true);
  switchTab('overview');
}
$('#login-form').addEventListener('submit', event => { event.preventDefault(); $('#login-error').textContent = ''; enter($('#admin-key').value.trim()).catch(error => { $('#login-error').textContent = error.message; adminKey = ''; }); });
$('#logout').addEventListener('click', () => { adminKey = ''; sessionStorage.removeItem(KEY); authScreen(); });
document.querySelectorAll('[data-tab]').forEach(button => button.addEventListener('click', () => switchTab(button.dataset.tab)));
$('#close-secret').onclick = $('#done-secret').onclick = () => $('#secret-dialog').close();
$('#copy-secret').onclick = () => navigator.clipboard.writeText($('#new-secret').textContent).then(() => flash('密钥已复制。'));
function reveal(secret) { $('#new-secret').textContent = secret; $('#secret-dialog').showModal(); }

async function loadOverview() {
  const [clientData, logData, config] = await Promise.all([
    json('/api/admin/clients?pageSize=1'), json('/api/admin/call-logs?pageSize=1'), json('/api/admin/settings')
  ]);
  $('#overview').innerHTML = `<div class="page-head"><div><p class="eyebrow">SERVICE OVERVIEW</p><h1>KaungMcp 管理</h1><p>客户端、调用记录和运行配置统一管理。</p></div><button class="button" id="overview-refresh">刷新</button></div>
  <div class="stats"><div class="panel stat"><span>客户端总数</span><strong>${clientData.totalCount}</strong><span>包含启用与停用客户端</span></div><div class="panel stat"><span>累计请求日志</span><strong>${logData.totalCount}</strong><span>按客户端连续保留</span></div><div class="panel stat"><span>待重启设置</span><strong>${config.pendingRestart.length}</strong><span>重启 KaungMcp 后生效</span></div></div>
  ${config.pendingRestart.length ? `<div class="notice warning">有 ${config.pendingRestart.length} 项配置待重启生效。当前请求仍使用页面标出的生效值。</div>` : '<div class="notice">配置已与运行状态同步。</div>'}
  <div class="panel"><h2>管理提示</h2><p class="muted">客户端密钥只在创建或轮换时展示。日志详情保存请求参数及接口输出，不采集 HTTP 鉴权头。</p><div class="actions"><button class="button primary" data-go="tools">管理工具</button><button class="button" data-go="clients">管理客户端</button><button class="button" data-go="logs">查看日志</button><button class="button" data-go="service">服务设置</button></div></div>`;
  $('#overview-refresh').onclick = () => run(loadOverview);
  $('#overview').querySelectorAll('[data-go]').forEach(button => button.onclick = () => switchTab(button.dataset.go));
}

const toolDetailDialog = $('#tool-detail-dialog');
const toolList = () => toolCatalog?.categories.flatMap(category => category.tools) || [];
const toolByName = name => toolList().find(tool => tool.name === name);
const pretty = value => JSON.stringify(value, null, 2);
function closeToolDetail() {
  toolTestController?.abort(); toolTestController = null; selectedToolName = ''; toolRawMode = false;
  if (toolDetailDialog?.open) toolDetailDialog.close();
}
$('#close-tool-detail').onclick = closeToolDetail;
toolDetailDialog.addEventListener('cancel', event => { event.preventDefault(); closeToolDetail(); });
function toolBadges(tool) {
  const badges = [tool.readOnly === true ? ['green','只读'] : tool.readOnly === false ? ['amber','可写'] : ['','读写未知']];
  if (tool.idempotent === true) badges.push(['','幂等']);
  if (tool.openWorld === true) badges.push(['','外部交互']);
  if (tool.destructive === true) badges.push(['red','破坏性']);
  return badges.map(([kind,label]) => `<span class="pill ${kind}">${label}</span>`).join('');
}
async function loadTools() {
  toolCatalog = await json('/api/admin/tools');
  renderTools();
}
function renderTools() {
  if (!toolCatalog) return;
  const query = toolSearch.trim().toLowerCase();
  const categories = toolCatalog.categories.map(category => ({...category, tools:category.tools.filter(tool => {
    const matchesText = !query || [tool.name,tool.title,tool.description].some(value => String(value || '').toLowerCase().includes(query));
    const matchesStatus = toolStatus === 'all' || (toolStatus === 'enabled' ? tool.isEnabled : !tool.isEnabled);
    return matchesText && matchesStatus;
  })}));
  if (!categories.some(category => category.id === toolCategory)) toolCategory = categories[0]?.id || '';
  const activeCategory = categories.find(category => category.id === toolCategory);
  const categoryTabs = categories.map(category => `<button class="tool-tab ${category.id===toolCategory?'active':''}" type="button" role="tab" aria-selected="${category.id===toolCategory}" data-tool-category="${esc(category.id)}"><span>${esc(category.displayName)}</span><span class="tool-tab-count">${category.tools.length}</span></button>`).join('');
  const toolCards = activeCategory?.tools.map(tool => `<article class="tool-card ${tool.isEnabled?'':'disabled'}"><div class="tool-card-head"><span class="pill ${tool.isEnabled?'green':'red'}">${tool.isEnabled?'已启用':'已停用'}</span><span class="muted">${tool.parameterCount} 个参数</span></div><button class="tool-card-main" data-tool-open="${esc(tool.name)}"><strong>${esc(tool.title || tool.name)}</strong><code>${esc(tool.name)}</code><p>${esc(tool.description || '暂无描述')}</p></button><div class="tool-badges">${toolBadges(tool)}</div><div class="tool-card-actions"><button class="button primary" data-tool-open="${esc(tool.name)}">查看与测试</button><button class="button ${tool.isEnabled?'danger':''}" data-tool-toggle="${esc(tool.name)}">${tool.isEnabled?'停用':'启用'}</button></div></article>`).join('') || '';
  const categoryHtml = categories.length ? `<div class="tool-tabs" role="tablist" aria-label="工具分类">${categoryTabs}</div><section class="tool-category" role="tabpanel"><div class="tool-category-head"><div><h2>${esc(activeCategory?.displayName || '')}</h2><p>${esc(activeCategory?.description || '')}</p></div><span class="pill">${activeCategory?.tools.length || 0} 个</span></div>${toolCards?`<div class="tool-grid">${toolCards}</div>`:'<div class="panel empty">当前分类没有符合条件的工具。</div>'}</section>` : '<div class="panel empty">暂无工具分类。</div>';
  $('#tools').innerHTML = `<div class="page-head"><div><p class="eyebrow">TOOL CATALOG</p><h1>工具管理</h1><p>查看 MCP 工具定义、控制客户端可用性，并直接测试真实工具实现。</p></div><button class="button" id="refresh-tools">刷新</button></div>
  <div class="stats tool-stats"><div class="panel stat"><span>分类</span><strong>${toolCatalog.categories.length}</strong><span>代码显式维护</span></div><div class="panel stat"><span>工具总数</span><strong>${toolCatalog.totalCount}</strong><span>SDK 实际注册</span></div><div class="panel stat"><span>已启用</span><strong>${toolCatalog.enabledCount}</strong><span>客户端可以发现</span></div><div class="panel stat"><span>已停用</span><strong>${toolCatalog.disabledCount}</strong><span>后台仍可测试</span></div></div>
  <div class="panel tool-filters"><label>搜索工具<input id="tool-search" value="${esc(toolSearch)}" placeholder="名称、标题或描述"></label><label>状态<select id="tool-status"><option value="all" ${toolStatus==='all'?'selected':''}>全部状态</option><option value="enabled" ${toolStatus==='enabled'?'selected':''}>已启用</option><option value="disabled" ${toolStatus==='disabled'?'selected':''}>已停用</option></select></label></div>
  ${categoryHtml}`;
  $('#refresh-tools').onclick = () => run(loadTools);
  $('#tool-search').oninput = event => { toolSearch = event.target.value; renderTools(); $('#tool-search').focus(); $('#tool-search').setSelectionRange(toolSearch.length,toolSearch.length); };
  $('#tool-status').onchange = event => { toolStatus = event.target.value; renderTools(); };
  $('#tools').querySelectorAll('[data-tool-category]').forEach(button => button.onclick = () => { toolCategory = button.dataset.toolCategory; renderTools(); });
  $('#tools').querySelectorAll('[data-tool-open]').forEach(button => button.onclick = () => openToolDetail(button.dataset.toolOpen));
  $('#tools').querySelectorAll('[data-tool-toggle]').forEach(button => button.onclick = () => run(() => toggleTool(button.dataset.toolToggle)));
}
async function toggleTool(name, keepDialog = false) {
  const tool = toolByName(name); if (!tool) return;
  if (tool.isEnabled && !confirm(`确定停用“${tool.title || tool.name}”吗？\n客户端将无法继续发现或调用它，已有缓存调用也会被拒绝。`)) return;
  await json(`/api/admin/tools/${encodeURIComponent(name)}/enabled`, {method:'PUT',body:JSON.stringify({isEnabled:!tool.isEnabled})});
  toolCatalog = await json('/api/admin/tools');
  flash(`工具已${tool.isEnabled?'停用':'启用'}。`);
  renderTools();
  if (keepDialog) openToolDetail(name);
}
function schemaProperties(tool) {
  const schema = tool.inputSchema || {}, required = new Set(schema.required || []);
  return Object.entries(schema.properties || {}).map(([name,definition]) => ({name,definition,required:required.has(name)}));
}
function schemaType(definition) {
  if (Array.isArray(definition.type)) return definition.type.filter(value => value !== 'null').join(' / ') || '任意';
  return definition.type || (definition.enum ? 'string' : '任意');
}
function schemaConstraints(definition) {
  const values = [];
  if (definition.default !== undefined) values.push(`默认 ${JSON.stringify(definition.default)}`);
  if (definition.enum) values.push(`可选 ${definition.enum.map(String).join('、')}`);
  for (const [key,label] of [['minimum','最小'],['maximum','最大'],['minLength','最短'],['maxLength','最长'],['pattern','格式']]) if (definition[key] !== undefined) values.push(`${label} ${definition[key]}`);
  if (definition.format) values.push(definition.format);
  return values.join(' · ') || '—';
}
function parameterControl(field, index) {
  const d = field.definition, type = schemaType(d), hasDefault = d.default !== undefined;
  const included = field.required || hasDefault, disabled = included ? '' : 'disabled';
  const include = field.required ? '<span class="required-mark">必填</span>' : `<label class="include-param"><input type="checkbox" data-param-include="${index}" ${included?'checked':''}>传递</label>`;
  let control;
  if (d.enum) control = `<select data-param-value="${index}" ${disabled}>${!field.required&&!hasDefault?'<option value="">请选择</option>':''}${d.enum.map(value=>`<option value="${esc(value)}" ${hasDefault&&String(d.default)===String(value)?'selected':''}>${esc(value)}</option>`).join('')}</select>`;
  else if (type === 'boolean') control = `<label class="boolean-control"><input type="checkbox" data-param-value="${index}" ${d.default===true?'checked':''} ${disabled}>是</label>`;
  else if (type === 'integer' || type === 'number') control = `<input type="number" data-param-value="${index}" value="${hasDefault?esc(d.default):''}" ${type==='integer'?'step="1"':'step="any"'} ${disabled}>`;
  else if (type.includes('object') || type.includes('array')) control = `<textarea data-param-value="${index}" rows="4" ${disabled}>${hasDefault?esc(pretty(d.default)):type.includes('array')?'[]':'{}'}</textarea>`;
  else control = `<input type="text" data-param-value="${index}" value="${hasDefault?esc(d.default):''}" ${disabled}>`;
  return `<div class="tool-param-field" data-param-index="${index}"><div class="tool-param-label"><strong>${esc(field.name)}</strong><span class="mono">${esc(type)}</span>${include}</div>${control}<small>${esc(d.description || '暂无参数说明')}${schemaConstraints(d)!=='—'?` · ${esc(schemaConstraints(d))}`:''}</small></div>`;
}
function openToolDetail(name) {
  const tool = toolByName(name); if (!tool) return;
  toolTestController?.abort(); toolTestController = null; selectedToolName = name; toolRawMode = false;
  const fields = schemaProperties(tool);
  const rows = fields.map(field => `<tr><td><strong>${esc(field.name)}</strong></td><td class="mono">${esc(schemaType(field.definition))}</td><td>${field.required?'是':'否'}</td><td>${esc(schemaConstraints(field.definition))}</td><td>${esc(field.definition.description || '—')}</td></tr>`).join('');
  $('#tool-detail-title').textContent = tool.title || tool.name;
  $('#tool-detail-content').innerHTML = `<div class="tool-detail-summary"><div><code>${esc(tool.name)}</code><p>${esc(tool.description || '暂无描述')}</p><div class="tool-badges">${toolBadges(tool)}</div></div><div class="actions"><span class="pill ${tool.isEnabled?'green':'red'}">${tool.isEnabled?'已启用':'已停用'}</span><button id="detail-toggle-tool" class="button ${tool.isEnabled?'danger':''}">${tool.isEnabled?'停用':'启用'}</button></div></div>
  <section class="tool-detail-section"><div class="panel-head"><h3>参数定义</h3><span class="muted">${fields.length} 个</span></div><div class="table-wrap"><table class="table"><thead><tr><th>参数</th><th>类型</th><th>必填</th><th>默认值 / 约束</th><th>描述</th></tr></thead><tbody>${rows||'<tr><td colspan="5" class="empty">此工具没有参数</td></tr>'}</tbody></table></div></section>
  <section class="tool-detail-section schema-details"><details><summary>输入 JSON Schema</summary><pre>${esc(pretty(tool.inputSchema))}</pre></details><details><summary>输出 JSON Schema</summary><pre>${tool.outputSchema?esc(pretty(tool.outputSchema)):'未声明结构化输出 Schema'}</pre></details></section>
  <section class="tool-detail-section tool-runner"><div class="panel-head"><div><h3>测试调用</h3><p class="muted">${tool.isEnabled?'调用真实工具实现。':'该工具对客户端已停用，但管理员仍可验证。'}</p></div><button id="toggle-tool-input" class="button" ${fields.length?'':'disabled'}>使用原始 JSON</button></div><form id="tool-test-form"><div id="tool-form-input" class="tool-parameter-grid">${fields.map(parameterControl).join('')||'<p class="muted">无需填写参数。</p>'}</div><div id="tool-raw-input" class="field hidden"><span>参数 JSON 对象</span><textarea id="tool-raw-json" rows="10">{}</textarea></div><p id="tool-test-status" class="muted" role="status" aria-live="polite"></p><div class="actions"><button id="run-tool-test" class="button primary" type="submit">执行测试</button><button id="cancel-tool-test" class="button hidden" type="button">取消</button></div></form><div id="tool-test-result"></div></section>`;
  bindToolDetail(tool, fields);
  if (!toolDetailDialog.open) toolDetailDialog.showModal();
}
function bindToolDetail(tool, fields) {
  $('#detail-toggle-tool').onclick = () => run(() => toggleTool(tool.name, true));
  $('#tool-detail-content').querySelectorAll('[data-param-include]').forEach(input => input.onchange = () => {
    const control = $(`[data-param-value="${input.dataset.paramInclude}"]`); control.disabled = !input.checked;
  });
  $('#toggle-tool-input').onclick = () => {
    try {
      if (!toolRawMode) $('#tool-raw-json').value = pretty(collectToolForm(fields));
      else applyRawToForm(fields);
      toolRawMode = !toolRawMode;
      show('#tool-form-input', !toolRawMode); show('#tool-raw-input', toolRawMode);
      $('#toggle-tool-input').textContent = toolRawMode ? '使用参数表单' : '使用原始 JSON';
      $('#tool-test-status').textContent = '';
    } catch (error) { $('#tool-test-status').textContent = error.message; }
  };
  $('#tool-test-form').onsubmit = event => { event.preventDefault(); runToolTest(tool, fields); };
  $('#cancel-tool-test').onclick = () => toolTestController?.abort();
}
function collectToolForm(fields) {
  const values = {};
  fields.forEach((field,index) => {
    const include = field.required || $(`[data-param-include="${index}"]`)?.checked;
    if (!include) return;
    const control = $(`[data-param-value="${index}"]`), type = schemaType(field.definition);
    if (type === 'boolean') values[field.name] = control.checked;
    else if (type === 'integer') { if (control.value === '') throw new Error(`${field.name} 不能为空。`); values[field.name] = Number.parseInt(control.value,10); }
    else if (type === 'number') { if (control.value === '') throw new Error(`${field.name} 不能为空。`); values[field.name] = Number(control.value); }
    else if (type.includes('object') || type.includes('array')) { try { values[field.name] = JSON.parse(control.value); } catch { throw new Error(`${field.name} 不是有效的 JSON。`); } }
    else { if (field.required && control.value === '') throw new Error(`${field.name} 不能为空。`); values[field.name] = control.value; }
  });
  return values;
}
function collectToolArguments(fields) {
  if (!toolRawMode) return collectToolForm(fields);
  let value; try { value = JSON.parse($('#tool-raw-json').value); } catch { throw new Error('参数不是有效的 JSON。'); }
  if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error('参数必须是 JSON 对象。');
  return value;
}
function applyRawToForm(fields) {
  let values; try { values = JSON.parse($('#tool-raw-json').value); } catch { throw new Error('参数不是有效的 JSON。'); }
  if (!values || Array.isArray(values) || typeof values !== 'object') throw new Error('参数必须是 JSON 对象。');
  fields.forEach((field,index) => {
    const has = Object.hasOwn(values,field.name), include = $(`[data-param-include="${index}"]`), control = $(`[data-param-value="${index}"]`);
    if (include) include.checked = has;
    control.disabled = !field.required && !has;
    if (!has) return;
    const type = schemaType(field.definition), value = values[field.name];
    if (type === 'boolean') control.checked = Boolean(value);
    else if (type.includes('object') || type.includes('array')) control.value = pretty(value);
    else control.value = value ?? '';
  });
}
function setToolTestBusy(busy) {
  $('#run-tool-test').disabled = busy; $('#toggle-tool-input').disabled = busy || !schemaProperties(toolByName(selectedToolName)).length;
  show('#cancel-tool-test', busy);
}
async function runToolTest(tool, fields) {
  const status = $('#tool-test-status'), resultBox = $('#tool-test-result');
  try {
    const argumentsValue = collectToolArguments(fields);
    if (tool.requiresRiskConfirmation && !confirm('此工具可能修改外部状态或执行破坏性操作。确认继续测试吗？')) return;
    toolTestController?.abort(); toolTestController = new AbortController(); setToolTestBusy(true);
    status.textContent = '正在执行工具…'; resultBox.replaceChildren();
    const data = await json(`/api/admin/tools/${encodeURIComponent(tool.name)}/test`, {method:'POST',signal:toolTestController.signal,body:JSON.stringify({arguments:argumentsValue,confirmRisk:tool.requiresRiskConfirmation})});
    status.textContent = `${data.succeeded?'执行成功':'执行失败'} · ${data.durationMilliseconds} ms · ${data.isEnabled?'工具已启用':'工具已停用'}`;
    resultBox.innerHTML = `<div class="tool-result-head"><span class="pill ${data.succeeded?'green':'red'}">${data.succeeded?'成功':'失败'}</span><div class="actions"><button id="copy-tool-result" class="button">复制结果</button><button id="view-tool-log" class="button">查看日志</button></div></div><pre class="tool-result-payload" tabindex="0">${esc(pretty(data.result))}</pre>`;
    $('#copy-tool-result').onclick = () => navigator.clipboard.writeText(pretty(data.result)).then(() => {status.textContent='结果已复制。';});
    $('#view-tool-log').onclick = () => { closeToolDetail(); selectedClient=''; logSource='admin_test'; logToolName=tool.name; logPage=1; switchTab('logs'); };
  } catch (error) {
    if (error.name === 'AbortError') status.textContent = '测试已取消，未写入调用日志。';
    else status.textContent = `测试失败：${error.message}`;
  } finally {
    toolTestController = null; setToolTestBusy(false);
  }
}

async function loadClients() {
  const data = await json(`/api/admin/clients?page=${clientPage}&pageSize=20`);
  clients = data.items;
  const rows = clients.map(client => `<tr><td><strong>${esc(client.name)}</strong><br><span class="muted">${esc(client.description || '')}</span></td><td><span class="pill ${client.isEnabled ? 'green' : 'red'}">${client.isEnabled ? '启用' : '停用'}</span></td><td class="mono">kmcp_${esc(client.keyId)}…</td><td>${date(client.lastUsedAtUtc)}</td><td class="actions"><button class="button" data-action="logs" data-id="${client.id}">日志</button><button class="button" data-action="rotate" data-id="${client.id}">轮换</button><button class="button" data-action="${client.isEnabled ? 'disable' : 'enable'}" data-id="${client.id}">${client.isEnabled ? '停用' : '启用'}</button><button class="button danger" data-action="delete" data-id="${client.id}">删除</button></td></tr>`).join('');
  $('#clients').innerHTML = `<div class="page-head"><div><p class="eyebrow">CLIENTS & KEYS</p><h1>客户端与密钥</h1><p>管理 MCP 调用身份；每个客户端当前只有一个有效密钥。</p></div></div>
  <div class="panel"><div class="panel-head"><h2>创建客户端</h2></div><form id="create-client" class="field-grid"><div class="field"><span>客户端名称</span><input name="name" minlength="2" maxlength="100" required placeholder="例如 my-agent"></div><div class="field"><span>说明</span><input name="description" maxlength="500" placeholder="可选"></div><div class="section-actions"><button class="button primary" type="submit">创建并生成密钥</button></div></form></div>
  <div class="panel"><div class="panel-head"><h2>客户端列表</h2><span class="muted">${data.totalCount} 个</span></div><div class="table-wrap"><table class="table"><thead><tr><th>客户端</th><th>状态</th><th>密钥标识</th><th>最近使用</th><th>操作</th></tr></thead><tbody>${rows || '<tr><td colspan="5" class="empty">暂无客户端</td></tr>'}</tbody></table></div>${pagination(data, 'client')}</div>`;
  $('#create-client').onsubmit = event => run(async () => {
    event.preventDefault(); const form = new FormData(event.target);
    const created = await json('/api/admin/clients', {method:'POST',body:JSON.stringify({name:form.get('name'),description:form.get('description')})});
    reveal(created.apiKey); flash('客户端已创建。'); await loadClients();
  });
  $('#clients').querySelectorAll('[data-action]').forEach(button => button.onclick = () => run(() => clientAction(button.dataset.action, button.dataset.id)));
  bindPages('#clients', 'client');
}
async function clientAction(action, id) {
  const client = clients.find(item => item.id === id); if (!client) return;
  if (action === 'logs') { selectedClient = id; logSource = ''; logToolName = ''; logPage = 1; switchTab('logs'); return; }
  const local = client.name.startsWith('kaung-agent-local');
  const warning = local ? '\n这是本地 Agent 客户端。轮换、停用或删除会立即中断 Agent 连接；轮换后需同步 .kaung/local.env 中的 KAUNG_MCP_API_KEY 并重启 Agent。' : '';
  if (!confirm(`确定要${{rotate:'轮换密钥',disable:'停用',enable:'启用',delete:'删除'}[action]}“${client.name}”吗？${warning}`)) return;
  if (action === 'delete') await api(`/api/admin/clients/${id}`, {method:'DELETE'});
  else if (action === 'rotate') { const result = await json(`/api/admin/clients/${id}/rotate-key`, {method:'POST'}); reveal(result.apiKey); }
  else await api(`/api/admin/clients/${id}/${action}`, {method:'POST'});
  flash('操作已完成。'); await loadClients();
}
function pagination(data, kind) { const page = data.page, pages = Math.max(1,Math.ceil(data.totalCount/data.pageSize)); return `<div class="pagination"><span>第 ${page} / ${pages} 页</span><button class="button" data-page="${page-1}" ${page<=1?'disabled':''}>上一页</button><button class="button" data-page="${page+1}" ${page>=pages?'disabled':''}>下一页</button></div>`; }
function bindPages(selector, kind) { $(selector).querySelectorAll('[data-page]').forEach(button => button.onclick = () => { if(kind==='client'){clientPage=Number(button.dataset.page);run(loadClients);}else{logPage=Number(button.dataset.page);run(loadLogs);} }); }

async function allClients() {
  const items = []; let page = 1, total = 0;
  do { const data = await json(`/api/admin/clients?page=${page}&pageSize=100&includeDeleted=true`); items.push(...data.items); total = data.totalCount; page++; } while (items.length < total);
  return items;
}
async function loadLogs() {
  const query = new URLSearchParams({page:String(logPage),pageSize:'20'});
  if (selectedClient) query.set('clientId',selectedClient);
  const method = $('#log-method')?.value || '', result = $('#log-result')?.value || '';
  logSource = $('#log-source')?.value ?? logSource;
  logToolName = $('#log-tool')?.value ?? logToolName;
  const from = $('#log-from')?.value || '', to = $('#log-to')?.value || '';
  if (method) query.set('method',method);
  if (logSource) query.set('source',logSource);
  if (logToolName) query.set('toolName',logToolName);
  if (result) query.set('succeeded',result);
  if (from) query.set('fromUtc',new Date(from).toISOString());
  if (to) query.set('toUtc',new Date(to).toISOString());
  const data = await json(`/api/admin/call-logs?${query}`);
  const clientData = await allClients();
  const options = clientData.map(item => `<option value="${item.id}" ${selectedClient===item.id?'selected':''}>${esc(item.name)}${item.deletedAtUtc?'（已删除）':''}</option>`).join('');
  const rows = data.items.map(item => `<tr><td>${date(item.startedAtUtc)}</td><td><div class="source-label"><strong>${esc(item.clientName)}</strong><small>${item.source==='admin_test'?'后台测试':item.source==='scheduler'?'定时任务':'MCP 客户端'}</small></div></td><td><strong>${esc(item.requestMethod)}</strong><br><span class="mono muted">${esc(item.targetName || '')}</span></td><td><span class="pill ${item.succeeded?'green':'red'}">${item.succeeded?'成功':'失败'}</span></td><td>${item.durationMilliseconds} ms</td><td title="${esc(item.errorMessage)}">${esc(item.errorCode || '—')}</td><td class="mono">${esc(item.traceId || '—')}</td><td><button class="button" data-log-detail="${item.id}">详情</button></td></tr>`).join('');
  $('#logs').innerHTML = `<div class="page-head"><div><p class="eyebrow">INVOCATION LOGS</p><h1>调用日志</h1><p>统一查看 MCP 客户端、后台测试和定时任务调用。</p></div><button class="button" id="refresh-logs">刷新</button></div><div class="panel"><div class="filters log-filters"><label>来源<select id="log-source"><option value="" ${!logSource?'selected':''}>全部来源</option><option value="mcp_client" ${logSource==='mcp_client'?'selected':''}>MCP 客户端</option><option value="admin_test" ${logSource==='admin_test'?'selected':''}>后台测试</option><option value="scheduler" ${logSource==='scheduler'?'selected':''}>定时任务</option></select></label><label>客户端<select id="log-client" ${logSource==='admin_test'||logSource==='scheduler'?'disabled':''}><option value="">全部客户端</option>${options}</select></label><label>工具名称<input id="log-tool" value="${esc(logToolName)}" placeholder="全部或输入工具"></label><label>请求方法<input id="log-method" list="mcp-methods" value="${esc(method)}" placeholder="全部或输入方法"><datalist id="mcp-methods">${['initialize','server/discover','ping','tools/list','tools/call','prompts/list','prompts/get','resources/list','resources/templates/list','resources/read'].map(value=>`<option value="${value}"></option>`).join('')}</datalist></label><label>结果<select id="log-result"><option value="">全部</option><option value="true" ${result==='true'?'selected':''}>成功</option><option value="false" ${result==='false'?'selected':''}>失败</option></select></label><label>开始时间<input id="log-from" type="datetime-local" value="${esc(from)}"></label><label>结束时间<input id="log-to" type="datetime-local" value="${esc(to)}"></label></div><div class="actions"><button class="button primary" id="apply-logs">筛选</button><button class="button" id="clear-logs">清除筛选</button></div></div><div class="panel"><div class="panel-head"><h2>请求记录</h2><span class="muted">${data.totalCount} 条</span></div><div class="table-wrap"><table class="table"><thead><tr><th>时间</th><th>来源 / 客户端</th><th>方法 / 目标</th><th>结果</th><th>耗时</th><th>错误</th><th>Trace ID</th><th>操作</th></tr></thead><tbody>${rows||'<tr><td colspan="8" class="empty">暂无调用记录</td></tr>'}</tbody></table></div>${pagination(data,'log')}</div>`;
  $('#log-source').onchange = event => {logSource=event.target.value;if(logSource==='admin_test'||logSource==='scheduler')selectedClient='';logPage=1;run(loadLogs);};
  $('#log-client').onchange = event => {selectedClient=event.target.value;if(selectedClient)logSource='mcp_client';logPage=1;run(loadLogs);};
  $('#refresh-logs').onclick = () => run(loadLogs);
  $('#apply-logs').onclick = () => {logPage=1;run(loadLogs);};
  $('#clear-logs').onclick = () => {selectedClient='';logSource='';logToolName='';logPage=1;$('#log-method').value='';$('#log-result').value='';$('#log-from').value='';$('#log-to').value='';run(loadLogs);};
  bindPages('#logs','log');
  $('#logs').querySelectorAll('[data-log-detail]').forEach(button => button.onclick = () => openLogDetail(button.dataset.logDetail));
}


let logDetailController = null, logDetailVersion = 0, logDetailId = null;
const logDetailDialog = $('#log-detail-dialog');
function clearLogDetail() {
  logDetailVersion++;
  logDetailController?.abort();
  logDetailController = null;
  logDetailId = null;
  $('#log-detail-content').replaceChildren();
  $('#log-detail-status').textContent = '';
  show('#retry-log-detail', false);
}
function closeLogDetail() {
  clearLogDetail();
  if (logDetailDialog.open) logDetailDialog.close();
}
$('#close-log-detail').onclick = closeLogDetail;
logDetailDialog.addEventListener('cancel', event => { event.preventDefault(); closeLogDetail(); });
// Ignore a queued close event if another detail was already opened.
logDetailDialog.addEventListener('close', () => { if (!logDetailDialog.open) clearLogDetail(); });
$('#retry-log-detail').onclick = () => { if (logDetailId) openLogDetail(logDetailId); };
function prettyPayload(value) {
  try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; }
}
async function openLogDetail(id) {
  clearLogDetail();
  logDetailId = id;
  const version = logDetailVersion;
  const controller = new AbortController();
  logDetailController = controller;
  if (!logDetailDialog.open) logDetailDialog.showModal();
  $('#log-detail-status').textContent = '正在加载详情…';
  try {
    const detail = await json(`/api/admin/call-logs/${encodeURIComponent(id)}`, {signal:controller.signal});
    if (version !== logDetailVersion || !logDetailDialog.open) return;
    const item = detail.log;
    const content = $('#log-detail-content');
    const metadata = document.createElement('dl');
    metadata.className = 'log-detail-metadata';
    for (const [label, value] of [
      ['来源',item.source === 'admin_test' ? '后台测试' : item.source === 'scheduler' ? '定时任务' : 'MCP 客户端'],['客户端',item.clientName],['请求方法',item.requestMethod],['目标',item.targetName || '—'],
      ['时间',date(item.startedAtUtc)],['耗时',`${item.durationMilliseconds} ms`],
      ['状态',item.succeeded ? '成功' : '失败'],['Trace ID',item.traceId || '—'],
      ...(item.errorCode ? [['错误',`${item.errorCode}：${item.errorMessage || ''}`]] : [])
    ]) {
      const term = document.createElement('dt'), definition = document.createElement('dd');
      term.textContent = label; definition.textContent = value;
      metadata.append(term, definition);
    }
    content.append(metadata);
    for (const [label, value, missing] of [
      ['请求参数',detail.requestParametersJson,'该记录未保存请求参数'],
      ['接口输出',detail.responseBody,detail.requestParametersJson === null ? '该记录未保存接口输出' : '本次调用未捕获到接口输出（可能已取消或连接中断）']
    ]) {
      const section = document.createElement('section'), header = document.createElement('div');
      section.className = 'log-payload'; header.className = 'panel-head';
      const title = document.createElement('h3'), copy = document.createElement('button');
      title.textContent = label; copy.textContent = '复制'; copy.className = 'button';
      copy.disabled = value === null;
      const formatted = value === null ? missing : prettyPayload(value);
      copy.onclick = async () => {
        try {
          await navigator.clipboard.writeText(formatted);
          if (version === logDetailVersion) $('#log-detail-status').textContent = `${label}已复制。`;
        } catch {
          if (version === logDetailVersion) $('#log-detail-status').textContent = '复制失败，请选中内容手动复制。';
        }
      };
      header.append(title, copy);
      const body = document.createElement('pre');
      body.textContent = formatted; body.tabIndex = 0; body.setAttribute('aria-label', label);
      section.append(header, body); content.append(section);
    }
    $('#log-detail-status').textContent = '';
  } catch (error) {
    if (version !== logDetailVersion || !logDetailDialog.open || error.name === 'AbortError') return;
    $('#log-detail-status').textContent = `详情加载失败：${error.message}`;
    show('#retry-log-detail', true);
  }
}

const groups = {
  search: {title:'搜索配置',subtitle:'SearXNG 搜索地址、开关及静态回退在保存后立即生效。容器启动设置位于“服务与安全”。',fields:[
    ['WebSearch:SearXng:Enabled','启用 SearXNG 搜索','bool'],['WebSearch:SearXng:BaseUrl','SearXNG 搜索地址','text'],['WebSearch:TimeoutSeconds','搜索超时（秒）','number'],['WebSearch:StaticFallback:Enabled','启用静态搜索回退','bool'],['WebSearch:StaticFallback:BaseUrl','静态回退地址','text'],['WebSearch:StaticFallback:TimeoutSeconds','回退超时（秒）','number'],['WebSearch:StaticFallback:MaxResponseBytes','回退响应上限（字节）','number'],['WebSearch:StaticFallback:UserAgent','回退 User-Agent','text']]},
  fetch: {title:'网页读取',subtitle:'以下设置保存后立即作用于后续请求。',fields:[
    ['WebFetch:TimeoutSeconds','读取超时（秒）','number'],['WebFetch:MaxResponseBytes','响应上限（字节）','number'],['WebFetch:MaxRedirects','最大重定向次数','number'],['WebFetch:BrowserFallbackEnabled','启用浏览器回退','bool'],['WebFetch:UserAgent','User-Agent','text'],['WebFetch:RobotsCacheMinutes','robots 缓存（分钟）','number']]},
  service: {title:'服务与安全',subtitle:'管理员密钥立即更换；监听、数据库、日志级别和 SearXNG 启动方式在重启后生效。',fields:[
    ['McpHost:LocalPort','本机 HTTP 端口','number'],['McpHost:LanEnabled','启用局域网 HTTPS','bool'],['McpHost:LanAddress','局域网私有 IPv4 地址','text'],['McpHost:LanPort','局域网 HTTPS 端口','number'],['AllowedHosts','允许的 Host','text'],['ConnectionStrings:KaungMcp','现有 SQLite 连接字符串','text'],['Logging:LogLevel:Default','默认日志级别','level'],['Logging:LogLevel:Microsoft.AspNetCore','ASP.NET Core 日志级别','level'],['SearXngLauncher:Mode','SearXNG 启动方式','mode'],['SearXngLauncher:Port','本地 SearXNG 端口','number'],['SearXngLauncher:ExternalEndpoint','外部 SearXNG 地址','text']]}
};
async function loadSettings() { settings = await json('/api/admin/settings'); dirty.clear(); renderSettings(tab); }
function field(key,label,type) {
  const desired = settings.desired[key] ?? '', effective = settings.effective[key] ?? '';
  const pending = settings.pendingRestart.includes(key), overridden = settings.overrides.includes(key);
  let control;
  if (type === 'bool') control = `<select data-setting="${key}"><option value="true" ${desired.toLowerCase()==='true'?'selected':''}>启用</option><option value="false" ${desired.toLowerCase()==='false'?'selected':''}>停用</option></select>`;
  else if (type === 'mode') control = `<select data-setting="${key}">${[['local','本地容器'],['external','外部服务'],['disabled','停用']].map(([v,t])=>`<option value="${v}" ${desired===v?'selected':''}>${t}</option>`).join('')}</select>`;
  else if (type === 'level') control = `<select data-setting="${key}">${['Trace','Debug','Information','Warning','Error','Critical','None'].map(v=>`<option value="${v}" ${desired===v?'selected':''}>${v}</option>`).join('')}</select>`;
  else control = `<input data-setting="${key}" type="${type}" value="${esc(desired)}">`;
  return `<div class="field ${pending?'pending':''}"><span>${label} ${pending?'<span class="pill amber">待重启</span>':''}</span><div class="field-row">${control}<button type="button" class="button" data-reset="${key}" ${overridden?'':'disabled'} title="恢复原始配置来源">恢复</button></div><small>当前生效：${esc(effective || '—')}${overridden?' · 页面覆盖':''}</small></div>`;
}
function renderSettings(name) {
  const group = groups[name]; if (!group || !settings) return;
  const pending = settings.pendingRestart.length;
  const extra = name === 'service' ? `<div class="panel"><h2>管理员密钥</h2><p class="muted">新密钥至少 32 位。轮换后会同步一键启动的本地配置，并更新当前标签页会话。</p><form id="rotate-admin" class="field-grid"><div class="field"><span>新管理员密钥</span><input id="new-admin-key" type="password" minlength="32" autocomplete="new-password" required></div><div class="section-actions"><button class="button danger" type="submit">轮换管理员密钥</button></div></form></div><div class="panel"><h2>局域网证书</h2><p class="muted">启用局域网 HTTPS 时自动生成自签证书。其他设备须手动信任公有证书。</p><div class="config-table"><div class="config-line"><strong>证书指纹</strong><code>${esc(settings.certificate?.thumbprint || '尚未生成')}</code></div><div class="config-line"><strong>有效期至</strong><span>${settings.certificate?.notAfterUtc ? date(settings.certificate.notAfterUtc) : '—'}</span></div></div><div class="section-actions"><button id="download-cert" class="button" ${settings.certificate?'':'disabled'}>下载公有证书</button></div></div>` : '';
  $(`#${name}`).innerHTML = `<div class="page-head"><div><p class="eyebrow">CONFIGURATION</p><h1>${group.title}</h1><p>${group.subtitle}</p></div></div>${pending?`<div class="notice warning">${pending} 项服务设置已保存，重启后生效。</div>`:''}<div class="panel"><div class="field-grid">${group.fields.map(([k,l,t])=>field(k,l,t)).join('')}</div><div class="section-actions"><button id="save-settings" class="button primary">保存修改</button></div></div>${extra}`;
  $(`#${name}`).querySelectorAll('[data-setting]').forEach(input => input.onchange = () => dirty.add(input.dataset.setting));
  $(`#${name}`).querySelectorAll('[data-reset]').forEach(button => button.onclick = () => run(async () => { settings = await json('/api/admin/settings',{method:'PUT',body:JSON.stringify({clear:[button.dataset.reset]})});flash('已恢复原始配置来源。');renderSettings(name); }));
  $('#save-settings').onclick = () => run(async () => {
    const changes = {}; for (const key of dirty) changes[key] = $(`[data-setting="${key}"]`).value;
    if (!Object.keys(changes).length) {flash('没有待保存的修改。');return;}
    settings = await json('/api/admin/settings',{method:'PUT',body:JSON.stringify({changes})});dirty.clear();flash('配置已保存。');renderSettings(name);
  });
  if (name === 'service') {
    $('#rotate-admin').onsubmit = event => {event.preventDefault();run(async()=>{const key=$('#new-admin-key').value;if(!confirm('确定轮换管理员密钥？旧密钥将立即失效。'))return;await api('/api/admin/admin-key/rotate',{method:'POST',body:JSON.stringify({newKey:key})});adminKey=key;sessionStorage.setItem(KEY,key);$('#new-admin-key').value='';flash('管理员密钥已轮换并同步本地启动配置。');});};
    $('#download-cert').onclick = () => run(async()=>{const response=await api('/api/admin/certificate');const blob=await response.blob();const url=URL.createObjectURL(blob);const link=document.createElement('a');link.href=url;link.download='kaungmcp-lan.cer';link.click();URL.revokeObjectURL(url);});
  }
}
if (adminKey) enter(adminKey).catch(() => authScreen()); else authScreen();
