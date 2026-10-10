let schedulerPage=1, schedulerTasks=[], schedulerCatalog=null, schedulerEditing=null;
const schedulerDialog=document.querySelector('#scheduler-dialog');
document.querySelector('#close-scheduler-dialog').onclick=()=>schedulerDialog.close();

async function loadScheduler(){
  const status=document.querySelector('#scheduler-status')?.value||'active';
  const type=document.querySelector('#scheduler-type')?.value||'';
  const target=document.querySelector('#scheduler-target')?.value||'';
  const query=new URLSearchParams({page:String(schedulerPage),pageSize:'20',status});
  if(type)query.set('executionType',type);if(target)query.set('target',target);
  const data=await json(`/api/admin/scheduled-tasks?${query}`);schedulerTasks=data.items;
  const rows=data.items.map(t=>`<tr><td><strong>${esc(t.name)}</strong><br><small>${esc(t.description||'')}${t.target==='jewelry.collect_sources'&&(t.arguments.sourceIds||[]).some(id=>['chowsangsang','lukfook','chowtaifook','laofengxiang','laomiao','caibai','chinagold','chowtaiseng'].includes(id))?'<br>如需第三方补齐，请编辑为品牌金价补齐任务；原计划已保留。':''}</small></td><td><span class="pill ${t.isArchived?'amber':t.isEnabled?'green':'red'}">${t.isArchived?'已归档':t.isEnabled?'已启用':'已停用'}</span></td><td>${t.executionType==='mcp_tool'?'MCP 工具':'内部处理器'}<br><code>${esc(t.target)}</code></td><td>${t.scheduleType==='interval'?`每 ${t.intervalMinutes} 分钟`:`<code>${esc(t.cronExpression)}</code>`}<br><small>${esc(t.timeZoneId)}</small></td><td>${date(t.nextRunAtUtc)}<br><small>最近：${esc(t.lastRunStatus||'暂无')}</small></td><td class="actions"><button class="button" data-sact="history" data-id="${t.id}">记录</button>${t.isArchived?`<button class="button" data-sact="restore" data-id="${t.id}">恢复</button>`:`<button class="button" data-sact="edit" data-id="${t.id}">编辑</button><button class="button" data-sact="run" data-id="${t.id}">立即执行</button><button class="button" data-sact="${t.isEnabled?'disable':'enable'}" data-id="${t.id}">${t.isEnabled?'停用':'启用'}</button><button class="button danger" data-sact="archive" data-id="${t.id}">归档</button>`}</td></tr>`).join('');
  document.querySelector('#scheduler').innerHTML=`<div class="page-head"><div><p class="eyebrow">SCHEDULER</p><h1>定时任务</h1><p>配置抓价与 MCP 工具计划，查看每次运行及错误。</p></div><div class="actions"><button id="scheduler-refresh" class="button">刷新</button><button id="scheduler-create" class="button primary">新建任务</button></div></div><div class="stats scheduler-stats"><div class="panel stat"><span>任务总数</span><strong>${data.totalCount}</strong></div><div class="panel stat"><span>已启用</span><strong>${data.enabledCount}</strong></div><div class="panel stat"><span>运行中</span><strong>${data.runningCount}</strong></div><div class="panel stat"><span>近 24 小时失败</span><strong>${data.recentFailedCount}</strong></div></div><div class="panel scheduler-filters"><label>状态<select id="scheduler-status">${[['active','未归档'],['enabled','已启用'],['disabled','已停用'],['archived','已归档'],['all','全部']].map(([v,l])=>`<option value="${v}" ${v===status?'selected':''}>${l}</option>`).join('')}</select></label><label>类型<select id="scheduler-type"><option value="">全部</option><option value="internal_handler" ${type==='internal_handler'?'selected':''}>内部处理器</option><option value="mcp_tool" ${type==='mcp_tool'?'selected':''}>MCP 工具</option></select></label><label>目标<input id="scheduler-target" value="${esc(target)}" placeholder="搜索目标名称"></label><button id="scheduler-filter" class="button">筛选</button></div><div class="panel"><div class="table-wrap"><table class="table"><thead><tr><th>任务</th><th>状态</th><th>目标</th><th>计划</th><th>下次 / 最近</th><th>操作</th></tr></thead><tbody>${rows||'<tr><td colspan="6" class="empty">暂无任务</td></tr>'}</tbody></table></div><div class="pagination"><span>第 ${data.page} / ${Math.max(1,Math.ceil(data.totalCount/data.pageSize))} 页</span><button id="scheduler-prev" class="button" ${data.page<=1?'disabled':''}>上一页</button><button id="scheduler-next" class="button" ${data.page*data.pageSize>=data.totalCount?'disabled':''}>下一页</button></div></div>`;
  document.querySelector('#scheduler-refresh').onclick=()=>run(loadScheduler);
  document.querySelector('#scheduler-create').onclick=()=>run(()=>openSchedulerEditor());
  document.querySelector('#scheduler-filter').onclick=()=>{schedulerPage=1;run(loadScheduler);};
  document.querySelector('#scheduler-prev').onclick=()=>{schedulerPage--;run(loadScheduler);};
  document.querySelector('#scheduler-next').onclick=()=>{schedulerPage++;run(loadScheduler);};
  document.querySelectorAll('[data-sact]').forEach(b=>b.onclick=()=>run(()=>schedulerAction(b.dataset.sact,b.dataset.id)));
}

async function schedulerAction(action,id){
  const task=schedulerTasks.find(x=>x.id===id);
  if(action==='edit')return openSchedulerEditor(task);
  if(action==='history')return openSchedulerHistory(task);
  if(action==='archive'&&!confirm(`归档“${task.name}”？运行历史会保留。`))return;
  if(action==='run'){
    const result=await json(`/api/admin/scheduled-tasks/${id}/run`,{method:'POST'});
    flash(`执行状态：${result.status}。${result.error||result.resultSummary||''}`);
  }else{
    await json(`/api/admin/scheduled-tasks/${id}${action==='archive'?'':`/${action}`}`,{method:action==='archive'?'DELETE':'POST'});
    flash('任务状态已更新。');
  }
  await loadScheduler();
}

async function openSchedulerEditor(task=null){
  schedulerCatalog=await json('/api/admin/scheduler/catalog');schedulerEditing=task;
  document.querySelector('#scheduler-dialog-title').textContent=task?'编辑定时任务':'新建定时任务';
  const kind=task?.executionType||'internal_handler';
  document.querySelector('#scheduler-dialog-content').innerHTML=`<form id="scheduler-form" class="scheduler-form"><div class="field-grid"><label class="field"><span>名称</span><input name="name" maxlength="120" value="${esc(task?.name||'')}" required></label><label class="field"><span>执行类型</span><select name="executionType"><option value="internal_handler" ${kind==='internal_handler'?'selected':''}>内部处理器</option><option value="mcp_tool" ${kind==='mcp_tool'?'selected':''}>MCP 工具</option></select></label></div><label class="field"><span>说明</span><input name="description" maxlength="1000" value="${esc(task?.description||'')}"></label><label class="field"><span>执行目标</span><select name="target"></select></label><div id="scheduler-arguments" class="panel"></div><div class="field-grid"><label class="field"><span>调度方式</span><select name="scheduleType"><option value="interval" ${task?.scheduleType!=='cron'?'selected':''}>固定周期</option><option value="cron" ${task?.scheduleType==='cron'?'selected':''}>Cron</option></select></label><label class="field"><span>时区</span><select name="timeZoneId">${schedulerCatalog.timeZones.map(z=>`<option value="${esc(z)}" ${z===(task?.timeZoneId||'Asia/Shanghai')?'selected':''}>${esc(z)}</option>`).join('')}</select></label></div><div class="field-grid"><label class="field" id="scheduler-interval"><span>周期（分钟，1–10080）</span><input name="intervalMinutes" type="number" min="1" max="10080" value="${task?.intervalMinutes||15}"></label><label class="field" id="scheduler-cron"><span>五段 Cron</span><input name="cronExpression" value="${esc(task?.cronExpression||'0 9 * * *')}" placeholder="分 时 日 月 星期"></label></div><label class="field"><span><input name="isEnabled" type="checkbox" ${task?.isEnabled!==false?'checked':''}> 启用任务</span></label><div class="actions"><button id="scheduler-preview" class="button" type="button">预览未来五次</button><button class="button primary" type="submit">保存任务</button></div><p id="scheduler-preview-result" class="muted" role="status"></p></form>`;
  const form=document.querySelector('#scheduler-form');
  form.elements.executionType.onchange=()=>{renderSchedulerTargets();renderSchedulerArguments();};
  form.elements.target.onchange=renderSchedulerArguments;
  form.elements.scheduleType.onchange=renderSchedulerSchedule;
  renderSchedulerTargets(task?.target);renderSchedulerArguments();renderSchedulerSchedule();
  document.querySelector('#scheduler-preview').onclick=()=>run(previewScheduler);
  form.onsubmit=e=>{e.preventDefault();run(saveScheduler);};
  schedulerDialog.showModal();
}

function renderSchedulerTargets(selected){
  const form=document.querySelector('#scheduler-form');
  const options=form.elements.executionType.value==='mcp_tool'
    ?schedulerCatalog.tools.filter(x=>x.isEnabled).map(x=>[x.name,x.title||x.name])
    :schedulerCatalog.internalHandlers.map(x=>[x.id,x.name]);
  form.elements.target.innerHTML=options.map(([id,label])=>`<option value="${esc(id)}" ${id===selected?'selected':''}>${esc(label)} (${esc(id)})</option>`).join('');
}
function renderSchedulerSchedule(){
  const cron=document.querySelector('#scheduler-form').elements.scheduleType.value==='cron';
  document.querySelector('#scheduler-interval').classList.toggle('hidden',cron);
  document.querySelector('#scheduler-cron').classList.toggle('hidden',!cron);
}
function renderSchedulerArguments(){
  const form=document.querySelector('#scheduler-form'),box=document.querySelector('#scheduler-arguments');
  const target=form.elements.target.value,existing=schedulerEditing?.target===target?schedulerEditing.arguments:{};
  if(form.elements.executionType.value==='internal_handler'){
    if(target==='jewelry.collect_brands'){box.innerHTML='<h3>品牌金价补齐</h3><p>先采官方报价，仅为当日缺失品类依次补采已验证网站。来源及错误仅供管理员查看，建议每 60 分钟执行。</p>';return;}
    box.innerHTML=`<h3>金价来源</h3><p class="muted">一个来源只能属于一个已启用任务。</p><div class="scheduler-sources">${schedulerCatalog.sources.map(s=>`<label><input type="checkbox" data-source-id="${esc(s.id)}" ${(existing.sourceIds||[]).includes(s.id)?'checked':''} ${s.enabled||(existing.sourceIds||[]).includes(s.id)?'':'disabled'}> <strong>${esc(s.name)}</strong> <code>${esc(s.id)}</code><small>${esc(s.enabled?'已验证':s.evidence)}</small></label>`).join('')}</div>`;return;
  }
  const tool=schedulerCatalog.tools.find(x=>x.name===target);
  const fields=Object.entries(tool?.inputSchema?.properties||{});
  box.innerHTML=`<h3>工具参数</h3><p class="muted">每次执行使用保存的参数。</p><div id="scheduler-schema-fields" class="field-grid">${fields.map(([name,def])=>`<label class="field"><span>${esc(name)}${tool.inputSchema.required?.includes(name)?' *':''}</span>${schemaType(def)==='boolean'?`<select data-scheduler-param="${esc(name)}"><option value="">省略</option><option value="false" ${existing[name]===false?'selected':''}>否</option><option value="true" ${existing[name]===true?'selected':''}>是</option></select>`:def.enum?`<select data-scheduler-param="${esc(name)}"><option value="">请选择</option>${def.enum.map(v=>`<option value="${esc(v)}" ${existing[name]===v?'selected':''}>${esc(v)}</option>`).join('')}</select>`:schemaType(def).includes('array')||schemaType(def).includes('object')?`<textarea data-scheduler-param="${esc(name)}" rows="3">${esc(existing[name]===undefined?'':JSON.stringify(existing[name]))}</textarea>`:`<input data-scheduler-param="${esc(name)}" value="${esc(existing[name]??'')}" placeholder="${esc(def.description||'')}">`}</label>`).join('')}</div><button type="button" id="scheduler-raw-toggle" class="button">切换原始 JSON</button><textarea id="scheduler-raw" class="hidden" rows="7">${esc(JSON.stringify(existing,null,2))}</textarea>`;
  document.querySelector('#scheduler-raw-toggle').onclick=()=>{
    const fields=document.querySelector('#scheduler-schema-fields'),raw=document.querySelector('#scheduler-raw');
    if(raw.classList.contains('hidden'))raw.value=JSON.stringify(schedulerFormArguments(),null,2);
    fields.classList.toggle('hidden');raw.classList.toggle('hidden');
  };
}
function schedulerFormArguments(){
  const form=document.querySelector('#scheduler-form'),tool=schedulerCatalog.tools.find(x=>x.name===form.elements.target.value),values={};
  for(const [name,def] of Object.entries(tool.inputSchema.properties||{})){
    const control=[...document.querySelectorAll('[data-scheduler-param]')].find(x=>x.dataset.schedulerParam===name);
    if(!control||control.value===''){if(tool.inputSchema.required?.includes(name))throw new Error(`${name} 是必填参数。`);continue;}
    const type=schemaType(def);
    values[name]=type==='boolean'?control.value==='true':type==='integer'?Number.parseInt(control.value,10):type==='number'?Number(control.value):type.includes('array')||type.includes('object')?JSON.parse(control.value):control.value;
  }
  return values;
}
function schedulerArguments(){
  if(document.querySelector('#scheduler-form').elements.target.value==='jewelry.collect_brands')return {};
  if(document.querySelector('#scheduler-form').elements.executionType.value==='internal_handler')
    return{sourceIds:[...document.querySelectorAll('[data-source-id]:checked')].map(x=>x.dataset.sourceId)};
  const raw=document.querySelector('#scheduler-raw');
  if(raw.classList.contains('hidden'))return schedulerFormArguments();
  const parsed=JSON.parse(raw.value);
  if(!parsed||Array.isArray(parsed)||typeof parsed!=='object')throw new Error('参数必须是 JSON 对象。');
  return parsed;
}
function schedulerScheduleRequest(){
  const f=document.querySelector('#scheduler-form'),cron=f.elements.scheduleType.value==='cron';
  return{scheduleType:f.elements.scheduleType.value,intervalMinutes:cron?null:Number(f.elements.intervalMinutes.value),
    cronExpression:cron?f.elements.cronExpression.value:null,timeZoneId:f.elements.timeZoneId.value};
}
async function previewScheduler(){
  const data=await json('/api/admin/scheduler/validate-schedule',{method:'POST',body:JSON.stringify(schedulerScheduleRequest())});
  const zone=document.querySelector('#scheduler-form').elements.timeZoneId.value;
  document.querySelector('#scheduler-preview-result').textContent=data.nextOccurrencesUtc.map(x=>new Intl.DateTimeFormat('zh-CN',{dateStyle:'short',timeStyle:'medium',timeZone:zone}).format(new Date(x))).join(' · ');
}
async function saveScheduler(){
  const f=document.querySelector('#scheduler-form'),tool=schedulerCatalog.tools.find(x=>x.name===f.elements.target.value);
  const risky=f.elements.executionType.value==='mcp_tool'&&tool?.requiresRiskConfirmation;
  if(risky&&!confirm('此工具可能修改或删除数据。保存后会按计划无人值守执行。确定授权此任务吗？'))return;
  const body={name:f.elements.name.value,description:f.elements.description.value,executionType:f.elements.executionType.value,
    target:f.elements.target.value,arguments:schedulerArguments(),...schedulerScheduleRequest(),isEnabled:f.elements.isEnabled.checked,confirmRisk:!!risky};
  await json(schedulerEditing?`/api/admin/scheduled-tasks/${schedulerEditing.id}`:'/api/admin/scheduled-tasks',
    {method:schedulerEditing?'PUT':'POST',body:JSON.stringify(body)});
  schedulerDialog.close();flash('任务已保存。');await loadScheduler();
}
async function openSchedulerHistory(task,page=1){
  const data=await json(`/api/admin/scheduled-tasks/${task.id}/runs?page=${page}&pageSize=20`);
  document.querySelector('#scheduler-dialog-title').textContent=`${task.name} · 运行历史`;
  document.querySelector('#scheduler-dialog-content').innerHTML=`<div class="table-wrap"><table class="table"><thead><tr><th>开始</th><th>触发</th><th>状态</th><th>耗时</th><th>详情</th></tr></thead><tbody>${data.items.map(x=>`<tr><td>${date(x.startedAtUtc)}</td><td>${esc(x.trigger)}</td><td><span class="pill ${x.status==='succeeded'?'green':x.status==='failed'||x.status==='blocked'?'red':'amber'}">${esc(x.status)}</span></td><td>${x.durationMilliseconds??'—'} ms</td><td><button class="button" data-run-detail="${x.id}">查看</button></td></tr>`).join('')||'<tr><td colspan="5" class="empty">暂无记录</td></tr>'}</tbody></table></div><div class="pagination"><button id="run-prev" class="button" ${page<=1?'disabled':''}>上一页</button><span>第 ${page} 页 · 共 ${data.totalCount} 条</span><button id="run-next" class="button" ${page*data.pageSize>=data.totalCount?'disabled':''}>下一页</button></div><div id="scheduler-run-detail"></div>`;
  document.querySelector('#run-prev').onclick=()=>run(()=>openSchedulerHistory(task,page-1));
  document.querySelector('#run-next').onclick=()=>run(()=>openSchedulerHistory(task,page+1));
  document.querySelectorAll('[data-run-detail]').forEach(b=>b.onclick=()=>run(async()=>{
    const detail=await json(`/api/admin/scheduled-task-runs/${b.dataset.runDetail}`);
    document.querySelector('#scheduler-run-detail').innerHTML=`<div class="panel"><h3>运行 #${detail.id}</h3><p>计划：${date(detail.plannedAtUtc)} · 开始：${date(detail.startedAtUtc)} · 完成：${date(detail.completedAtUtc)}</p><p>状态：${esc(detail.status)} · 耗时：${detail.durationMilliseconds??'—'} ms</p><p>${esc(detail.error||'')}</p><pre class="scheduler-result">${esc(detail.resultSummary||'无结果摘要')}</pre>${detail.invocationLogId?`<button id="scheduler-open-log" class="button">查看调用日志 #${detail.invocationLogId}</button>`:''}</div>`;
    if(detail.invocationLogId)document.querySelector('#scheduler-open-log').onclick=()=>{
      schedulerDialog.close();selectedClient='';logSource='scheduler';logToolName='';logPage=1;
      switchTab('logs');run(()=>openLogDetail(detail.invocationLogId));
    };
  }));
  if(!schedulerDialog.open)schedulerDialog.showModal();
}
