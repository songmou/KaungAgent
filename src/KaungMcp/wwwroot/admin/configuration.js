let settingsDefinitions = [], settingsDraft = new Map(), settingsClears = new Set(), configGeneration = 0;
let webSection = 'search';
function hasConfigChanges() { return settingsDraft.size > 0 || settingsClears.size > 0; }
function leaveConfiguration() {
  if (!hasConfigChanges()) return true;
  if (!confirm('还有未保存的配置修改。确定放弃修改并离开吗？')) return false;
  settingsDraft.clear(); settingsClears.clear(); return true;
}
window.addEventListener('beforeunload', e => { if (hasConfigChanges()) { e.preventDefault(); e.returnValue = ''; } });
function sectionTabs(items, active, attribute) { return `<div class="section-tabs" role="tablist">${items.map(([id,name])=>`<button class="section-tab ${active===id?'active':''}" role="tab" aria-selected="${active===id}" ${attribute}="${esc(id)}">${esc(name)}</button>`).join('')}</div>`; }
function settingControl(s, value) {
  const disabled = s.readOnlyReason ? 'disabled' : '', attr = `data-setting="${esc(s.key)}" ${disabled}`;
  if(s.type==='bool') return `<label class="switch-control"><input type="checkbox" ${attr} ${String(value).toLowerCase()==='true'?'checked':''}><span class="switch-track"></span><span data-switch-label>${String(value).toLowerCase()==='true'?'启用':'停用'}</span></label>`;
  if(s.type==='select') return `<select ${attr}>${(s.options||[]).map(o=>`<option value="${esc(o.value)}" ${value===o.value?'selected':''}>${esc(o.label)}</option>`).join('')}</select>`;
  return `<input ${attr} type="${['number','url'].includes(s.type)?s.type:'text'}" value="${esc(value)}" ${s.minimum!=null?`min="${s.minimum}"`:''} ${s.maximum!=null?`max="${s.maximum}"`:''} ${s.type==='number'?`step="${s.step??1}"`:''}>`;
}
function settingDisplay(s,value){if(s.type==='bool')return String(value).toLowerCase()==='true'?'启用':'停用';if(s.options)return s.options.find(o=>o.value===value)?.label??value;return value;}
function configField(item) {
  const s=item.definition, key=s.key, value=settingsDraft.get(key)??(settingsClears.has(key)?item.baselineValue:settings.desired[key]??s.defaultValue);
  const pending=settings.pendingRestart.includes(key), override=settings.overrides.includes(key), changed=settingsDraft.has(key)||settingsClears.has(key);
  return `<div class="config-field ${pending?'pending':''}"><div class="config-field-title"><label for="setting-${esc(key)}">${esc(s.name)}${s.unit?` <small>（${esc(s.unit)}）</small>`:''}</label><span class="pill ${pending?'amber':''}">${pending?'待重启':s.runtime?'即时生效':'重启生效'}</span></div><p class="field-description">${esc(s.description||'')}</p><div class="field-row">${settingControl(s,value)}<button type="button" class="button" data-reset="${esc(key)}" ${!override&&!changed?'disabled':''}>恢复来源</button></div>${s.readOnlyReason?`<p class="field-reason">${esc(s.readOnlyReason)}</p>`:''}<small data-setting-status>当前生效：${esc(settingDisplay(s,settings.effective[key]??s.defaultValue))} · ${settingsClears.has(key)?'待恢复来源':override?'页面覆盖':'原始来源'}${changed?' · 未保存':''}</small><details class="setting-advanced"><summary>配置键与范围</summary><code>${esc(key)}</code>${s.minimum!=null||s.maximum!=null?`<p>允许范围：${s.minimum??'不限'} ～ ${s.maximum??'不限'} ${esc(s.unit||'')}</p>`:''}</details></div>`;
}
function renderConfiguration(target, items) {
  const groups = new Map(); items.forEach(item=>{const id=item.definition.groupId; if(!groups.has(id))groups.set(id,{name:item.definition.groupName,items:[]});groups.get(id).items.push(item);});
  target.innerHTML=`<form class="configuration-form">${[...groups.values()].map(g=>`<section class="panel"><h2>${esc(g.name)}</h2><div class="configuration-grid">${g.items.map(configField).join('')}</div></section>`).join('')||'<div class="panel empty">没有可维护的配置；插件停用或未加载时，启用后重启可加载配置定义。</div>'}${items.length?'<div class="save-bar"><span data-draft-count></span><button class="button primary" type="submit">保存修改</button></div>':''}</form>`;
  for(const input of target.querySelectorAll('[data-setting]')){
    input.id=`setting-${input.dataset.setting}`;
    input.oninput=()=>{
      const key=input.dataset.setting,value=input.type==='checkbox'?String(input.checked):input.value;
      settingsClears.delete(key);
      if(value===(input.type==='checkbox'?String(settings.desired[key]??'').toLowerCase():String(settings.desired[key]??'')))settingsDraft.delete(key);else settingsDraft.set(key,value);
      if(input.type==='checkbox')input.parentElement.querySelector('[data-switch-label]').textContent=input.checked?'启用':'停用';
      input.closest('.config-field').querySelector('[data-setting-status]').textContent=`当前生效：${settingDisplay(items.find(s=>s.definition.key===key).definition,settings.effective[key]??'—')} · ${settingsDraft.has(key)?'未保存':'已同步'}`;
      input.closest('.config-field').querySelector('[data-reset]').disabled=false;updateDraftCount(target);
    };
  }
  target.querySelectorAll('[data-reset]').forEach(b=>b.onclick=()=>{
    const key=b.dataset.reset;settingsDraft.delete(key);settingsClears.add(key);renderConfiguration(target,items);
  });
  target.querySelector('form').onsubmit=e=>{e.preventDefault();run(async()=>{
    if(!hasConfigChanges()){flash('没有待保存的修改。');return;}
    const button=target.querySelector('[type=submit]');button.disabled=true;
    try{
      settings=await json('/api/admin/settings',{method:'PUT',body:JSON.stringify({changes:Object.fromEntries(settingsDraft),clear:[...settingsClears]})});
      settingsDraft.clear();settingsClears.clear();renderConfiguration(target,items);flash('配置已保存。');
    }finally{if(button.isConnected)button.disabled=false;}
  });};
  updateDraftCount(target);
}
function updateDraftCount(target){const label=target.querySelector('[data-draft-count]');if(label)label.textContent=hasConfigChanges()?`${settingsDraft.size+settingsClears.size} 项修改待保存`:'配置已同步';}
async function configurationData(){const data=await Promise.all([json('/api/admin/settings'),json('/api/admin/settings/definitions')]);return {settings:data[0],definitions:data[1]};}
async function loadSystemSettings(){
  const generation=++configGeneration, data=await configurationData();if(tab!=='service'||generation!==configGeneration)return;
  settings=data.settings;settingsDefinitions=data.definitions;
  $('#service').innerHTML=`<div class="page-head"><div><h1>系统设置</h1><p>所属：宿主系统。公共网络设置同时影响网页能力及使用它的业务插件。</p></div><button class="button" id="settings-refresh">刷新</button></div><div id="system-configuration"></div><div class="panel"><h2>管理员密钥</h2><p class="muted">新密钥至少 32 位，保存后立即生效。</p><form id="rotate-admin" class="field-grid"><label class="field"><span>新管理员密钥</span><input id="new-admin-key" type="password" minlength="32" autocomplete="new-password" required></label><div><button class="button danger" type="submit">轮换管理员密钥</button></div></form></div><div class="panel"><h2>局域网证书</h2><p>证书指纹：<code>${esc(settings.certificate?.thumbprint||'尚未生成')}</code></p><p>有效期至：${date(settings.certificate?.notAfterUtc)}</p><button id="download-cert" class="button" ${settings.certificate?'':'disabled'}>下载公有证书</button></div>`;
  renderConfiguration($('#system-configuration'),settingsDefinitions.filter(s=>s.ownerId==='host'));
  $('#settings-refresh').onclick=()=>{if(leaveConfiguration())run(loadSystemSettings);};
  $('#rotate-admin').onsubmit=e=>{e.preventDefault();run(async()=>{const key=$('#new-admin-key').value;if(!confirm('确定轮换管理员密钥？旧密钥将立即失效。'))return;await api('/api/admin/admin-key/rotate',{method:'POST',body:JSON.stringify({newKey:key})});adminKey=key;sessionStorage.setItem(KEY,key);$('#new-admin-key').value='';flash('管理员密钥已轮换。');});};
  $('#download-cert').onclick=()=>run(async()=>{const response=await api('/api/admin/certificate'),url=URL.createObjectURL(await response.blob()),link=document.createElement('a');link.href=url;link.download='kaungmcp-lan.cer';link.click();URL.revokeObjectURL(url);});
}
async function loadWeb(){
  const generation=++configGeneration,[module,data]=await Promise.all([json('/api/admin/modules/web'),configurationData()]);if(tab!=='web'||generation!==configGeneration)return;
  settings=data.settings;settingsDefinitions=data.definitions;
  $('#web').innerHTML=`<div class="page-head"><div><p class="eyebrow">HOST MODULE</p><h1>网页能力</h1><p>宿主内置 · 搜索与网页读取分别控制。</p></div><button class="button" id="web-refresh">刷新</button></div>${module.legacyPackagePresent?'<div class="notice">旧网页插件包已跳过加载，配置和历史已由内置网页模块接管。</div>':''}<div class="web-tool-grid">${module.tools.map(t=>`<article class="panel"><div class="panel-head"><h2>${t.name==='kaung_web_search'?'网页搜索':'网页读取'}</h2><span class="pill ${t.isEnabled?'green':'amber'}">${t.isEnabled?'已启用':'已停用'}</span></div><code>${esc(t.name)}</code><div class="actions section-actions"><button class="button primary" data-web-test="${esc(t.name)}">查看与测试</button><button class="button" data-web-toggle="${esc(t.name)}">${t.isEnabled?'停用':'启用'}</button></div></article>`).join('')}</div>${sectionTabs([['search','搜索'],['fetch','网页读取'],['dependencies','依赖服务']],webSection,'data-web-section')}<div class="notice">公共网络请求设置由系统统一维护，影响网页能力及珠宝采集。<button id="web-network" class="button">查看公共网络设置</button></div>${webSection==='dependencies'?'<div class="notice warning">依赖服务由启动脚本按工具开关准备。容器启动设置保存后需重启启动脚本；首次启用浏览器回退后也需重启启动脚本以准备 Chromium。</div>':''}<div id="web-configuration"></div><div class="actions"><button class="button" id="web-plans">网页定时任务</button><button class="button" id="web-records">网页执行记录</button></div>`;
  renderConfiguration($('#web-configuration'),settingsDefinitions.filter(s=>s.ownerId==='web'&&s.definition.groupId===webSection));
  $('#web-refresh').onclick=()=>{if(leaveConfiguration())run(loadWeb);};
  $('#web').querySelectorAll('[data-web-section]').forEach(b=>b.onclick=()=>{if(!leaveConfiguration())return;webSection=b.dataset.webSection;run(loadWeb);});
  $('#web').querySelectorAll('[data-web-test]').forEach(b=>b.onclick=()=>run(()=>openToolDetail(b.dataset.webTest)));
  $('#web').querySelectorAll('[data-web-toggle]').forEach(b=>b.onclick=()=>run(async()=>{if(!leaveConfiguration())return;const t=module.tools.find(t=>t.name===b.dataset.webToggle);await json(`/api/admin/tools/${encodeURIComponent(t.name)}/enabled`,{method:'PUT',body:JSON.stringify({isEnabled:!t.isEnabled})});await loadWeb();flash(t.isEnabled?'工具已停用，关联计划已阻塞。':'工具已启用。');}));
  $('#web-network').onclick=()=>{if(switchTab('service'))run(async()=>{await loadSystemSettings();$('#service [data-setting="WebFetch:TimeoutSeconds"]')?.scrollIntoView({block:'center',behavior:'smooth'});});};
  $('#web-plans').onclick=()=>{if(!leaveConfiguration())return;schedulerPlugin='web';schedulerPage=1;switchTab('scheduler');};
  $('#web-records').onclick=()=>openOwnerRecords('web');
}
function openOwnerRecords(id){if(!leaveConfiguration())return;taskRunsPlugin=id;taskRunsPlan='';taskRunsPage=1;logOwner=id;logPage=1;recordsSection='runs';switchTab('records');}
let recordsSection='runs';
function loadRecords(){
  $('#record-tabs').innerHTML=sectionTabs([['runs','任务运行'],['logs','调用日志']],recordsSection,'data-record-section');
  $('#runs').classList.toggle('hidden',recordsSection!=='runs');$('#logs').classList.toggle('hidden',recordsSection!=='logs');
  $('#record-tabs').querySelectorAll('[data-record-section]').forEach(b=>b.onclick=()=>{recordsSection=b.dataset.recordSection;loadRecords();});
  if(recordsSection==='runs')run(loadTaskRuns);else run(loadLogs);
}
