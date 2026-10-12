let pluginCatalog = [], pluginDetailGeneration = 0, selectedPluginId = '', pluginSection = 'overview';
const pluginState = p => p.pendingRestart?'待重启':p.available?'已启用':p.status==='missing'?'插件缺失':p.status==='failed'?'加载失败':'已停用';
async function loadPlugins() {
  const generation=++pluginDetailGeneration;
  const catalog=await json('/api/admin/plugins');if(tab!=='plugins'||generation!==pluginDetailGeneration)return;pluginCatalog=catalog;
  if(selectedPluginId&&pluginCatalog.some(p=>p.id===selectedPluginId))return openPluginDetail(selectedPluginId);
  selectedPluginId='';
  $('#plugins').innerHTML=`<div class="page-head"><div><h1>插件管理</h1><p>维护业务与扩展插件。网页搜索和读取由宿主的“网页能力”提供。</p></div><button class="button" id="plugins-refresh">刷新</button></div><div class="tool-grid">${pluginCatalog.map(p=>`<article class="panel plugin-card"><div class="panel-head"><h2>${esc(p.name)}</h2><span class="pill ${p.available?'green':'amber'}">${esc(pluginState(p))}</span></div><p class="muted"><code>${esc(p.id)}</code> · ${esc(p.version)}</p><p>${p.tools.length} 个工具 · ${p.taskCount} 个任务处理器</p>${p.error?`<p class="error-text">${esc(p.error)}</p>`:''}<div class="actions"><button class="button primary" data-plugin-detail="${esc(p.id)}">查看详情</button><button class="button" data-plugin-toggle="${esc(p.id)}">${p.isEnabled?'停用':'启用'}</button></div></article>`).join('')||'<div class="panel empty">未发现插件。安装插件包后重启服务。</div>'}</div>`;
  $('#plugins-refresh').onclick=()=>run(loadPlugins);
  $('#plugins').querySelectorAll('[data-plugin-detail]').forEach(b=>b.onclick=()=>{pluginSection='overview';run(()=>openPluginDetail(b.dataset.pluginDetail));});
  bindPluginToggles();
}
function bindPluginToggles(){ $('#plugins').querySelectorAll('[data-plugin-toggle]').forEach(b=>b.onclick=()=>run(async()=>{
  if(!leaveConfiguration())return;
  const p=pluginCatalog.find(p=>p.id===b.dataset.pluginToggle);b.disabled=true;
  await json(`/api/admin/plugins/${encodeURIComponent(p.id)}/enabled`,{method:'PUT',body:JSON.stringify({isEnabled:!p.isEnabled})});
  await loadPlugins();flash(p.isEnabled?'插件已停用；正在执行的任务允许完成。':'已保存启用状态；未加载的插件需重启。');
}));}
async function openPluginDetail(id){
  const generation=++pluginDetailGeneration;
  selectedPluginId=id;
  const [definitions,plans,runs,data,catalog]=await Promise.all([
    json(`/api/admin/plugins/${encodeURIComponent(id)}/task-definitions`),json(`/api/admin/scheduled-tasks?ownerId=${encodeURIComponent(id)}&pageSize=100`),
    json(`/api/admin/task-runs?ownerId=${encodeURIComponent(id)}&pageSize=5`),configurationData(),json('/api/admin/tools')]);
  if(tab!=='plugins'||generation!==pluginDetailGeneration)return;
  const p=pluginCatalog.find(p=>p.id===id);if(!p)return;
  settings=data.settings;settingsDefinitions=data.definitions;
  const tools=catalog.categories.flatMap(c=>c.tools).filter(t=>t.ownerId===id);
  $('#plugins').innerHTML=`<div class="plugin-detail-header"><button id="plugin-back" class="button">返回插件列表</button><div class="page-head"><div><p class="eyebrow">PLUGIN</p><h1>${esc(p.name)}</h1><p><code>${esc(id)}</code> · 版本 ${esc(p.version)} · <span class="pill ${p.available?'green':'amber'}">${esc(pluginState(p))}</span></p></div><div class="actions"><button class="button" id="plugin-refresh">刷新</button><button class="button" data-plugin-toggle="${esc(id)}">${p.isEnabled?'停用':'启用'}</button></div></div>${sectionTabs([['overview','概览'],['configuration','配置'],['tools','工具'],['plans','定时任务'],['records','执行记录']],pluginSection,'data-plugin-section')}</div><div id="plugin-detail-content"></div>`;
  const content=$('#plugin-detail-content');
  if(pluginSection==='configuration')renderConfiguration(content,settingsDefinitions.filter(s=>s.ownerId===id));
  if(pluginSection==='overview')content.innerHTML=`${p.error?`<div class="notice warning">${esc(p.error)}</div>`:''}<div class="stats"><div class="panel stat"><span>工具</span><strong>${tools.length}</strong></div><div class="panel stat"><span>任务处理器</span><strong>${p.taskCount}</strong></div><div class="panel stat"><span>已保存计划</span><strong>${plans.totalCount}</strong></div></div><div class="panel"><h2>运行状态</h2>${p.health.length?p.health.map(h=>`<p><span class="pill ${h.status==='ready'?'green':'amber'}">${h.status==='ready'?'正常':'依赖异常'}</span> ${esc(h.detail||'')}</p>`).join(''):'<p class="muted">该插件暂无依赖健康检查信息。</p>'}<p class="muted">安装、升级、卸载需要停止服务后调整插件目录，再启动。配置、计划及历史记录会保留。</p></div><div class="panel"><h2>任务处理器</h2>${(definitions.tasks||[]).map(t=>`<div class="detail-row"><strong>${esc(t.name)}</strong><small>${esc(t.id)} · 定义版本 ${t.version} · 默认超时 ${t.defaultTimeoutSeconds} 秒 · ${t.idempotent?'允许重试':'不允许重试'}</small></div>`).join('')||'<p class="muted">没有后台处理器。</p>'}</div>`;
  if(pluginSection==='tools')content.innerHTML=`<div class="tool-grid">${tools.map(t=>`<article class="panel"><h2>${esc(t.title||t.name)}</h2><code>${esc(t.name)}</code><p class="muted">${esc(t.description)}</p><button class="button primary" data-plugin-tool="${esc(t.name)}">查看与测试</button></article>`).join('')||'<div class="panel empty">暂无已加载工具。</div>'}</div>`;
  if(pluginSection==='plans')content.innerHTML=`<div class="panel"><div class="panel-head"><h2>当前计划</h2><button id="plugin-plans" class="button primary">管理此插件的计划</button></div>${plans.items.map(p=>`<div class="detail-row"><strong>${esc(p.name)}</strong><small>${esc(p.blockedReason|| (p.isEnabled?'已启用':'已停用'))} · ${p.scheduleType==='interval'?`每 ${p.intervalMinutes} 分钟`:esc(p.cronExpression)}</small></div>`).join('')||'<p class="muted">暂无计划。</p>'}</div><div class="panel"><h2>默认计划模板</h2>${(definitions.templates||[]).map(t=>`<div class="detail-row"><strong>${esc(t.name)}</strong><small>${t.scheduleType==='cron'?esc(t.cronExpression):`每 ${t.intervalMinutes} 分钟`} · ${t.isEnabled?'默认启用':'默认停用'}</small></div>`).join('')||'<p class="muted">没有默认计划模板。</p>'}</div>`;
  if(pluginSection==='records')content.innerHTML=`<div class="panel"><div class="panel-head"><h2>最近执行</h2><button id="plugin-records" class="button primary">查看全部执行记录</button></div>${runs.items.map(r=>`<div class="detail-row record-preview"><div><strong>${esc(r.taskName||r.targetName||r.targetId)}</strong><small>${date(r.createdAtUtc)} · ${esc(runLabel(r.status))}</small><p>${esc(r.summary||r.error||'—')}</p></div><button class="button" data-plugin-run="${r.id}">详情</button></div>`).join('')||'<p class="muted">暂无执行记录。</p>'}</div>`;
  $('#plugin-back').onclick=()=>{if(!leaveConfiguration())return;selectedPluginId='';run(loadPlugins);};
  $('#plugin-refresh').onclick=()=>{if(leaveConfiguration())run(loadPlugins);};
  $('#plugins').querySelectorAll('[data-plugin-section]').forEach(b=>b.onclick=()=>{if(!leaveConfiguration())return;pluginSection=b.dataset.pluginSection;run(()=>openPluginDetail(id));});
  content.querySelectorAll('[data-plugin-tool]').forEach(b=>b.onclick=()=>run(()=>openToolDetail(b.dataset.pluginTool)));
  content.querySelectorAll('[data-plugin-run]').forEach(b=>b.onclick=()=>run(()=>openTaskRun(b.dataset.pluginRun)));
  if($('#plugin-plans'))$('#plugin-plans').onclick=()=>{if(!leaveConfiguration())return;schedulerPlugin=id;schedulerPage=1;switchTab('scheduler');};
  if($('#plugin-records'))$('#plugin-records').onclick=()=>openOwnerRecords(id);
  bindPluginToggles();
}
