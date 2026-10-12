let schedulerPage=1, schedulerPlugin='', schedulerStatus='', schedulerTasks=[], schedulerTargets=[], schedulerEditing=null;
let taskRunsPage=1, taskRunsPlugin='', taskRunsPlan='', runDetailId=null;
let runsStatus='', runsTrigger='', runsFrom='', runsTo='', runsCorrelation='', runsGeneration=0, runDetailGeneration=0, runDetailLoading=false, runDetailActive=false;
const schedulerDialog=document.querySelector('#scheduler-dialog');
$('#close-scheduler-dialog').onclick=()=>{runDetailId=null;schedulerDialog.close();};
const runLabel=s=>({queued:'排队中',running:'执行中',succeeded:'成功',failed:'失败',cancelled:'已取消',timed_out:'已超时',interrupted:'已中断',skipped:'已跳过'})[s]||s||'—';
const triggerLabel=s=>({manual:'手动执行',scheduled:'定时执行',plugin:'插件请求'})[s]||s;
function schedulerParameterSchema(s){if(s.anyOf)s=s.anyOf.find(x=>x.type!=='null')||s;return Array.isArray(s.type)?{...s,type:s.type.find(x=>x!=='null')}:s;}
const planStatus=p=>p.blockedReason?`已阻塞：${p.blockedReason}`:p.isEnabled?'已启用':'已停用';
async function loadScheduler(){
  const [data, plugins] = await Promise.all([json(`/api/admin/scheduled-tasks?page=${schedulerPage}&pageSize=20&pluginId=${encodeURIComponent(schedulerPlugin)}&status=${schedulerStatus}`), json('/api/admin/capability-owners')]);
  schedulerTasks=data.items;
  $('#scheduler').innerHTML=`<div class="page-head"><div><h1>定时任务</h1><p>插件提供任务处理器与默认计划，也可为允许调度的工具创建计划。</p></div><div class="actions"><button class="button" id="scheduler-refresh">刷新</button><button class="button primary" id="scheduler-create">新建计划</button></div></div><div class="panel filters"><label>所属能力 / 插件<select id="scheduler-plugin"><option value="">全部</option>${plugins.map(p=>`<option value="${esc(p.id)}" ${p.id===schedulerPlugin?'selected':''}>${esc(p.name)}</option>`).join('')}</select></label><label>状态<select id="scheduler-status">${[['','全部'],['enabled','已启用'],['disabled','已停用'],['blocked','已阻塞']].map(([v,l])=>`<option value="${v}" ${v===schedulerStatus?'selected':''}>${l}</option>`).join('')}</select></label></div><div class="panel table-wrap"><table class="table"><thead><tr><th>计划</th><th>状态</th><th>时间</th><th>最近执行</th><th>操作</th></tr></thead><tbody>${data.items.map(p=>`<tr><td><strong>${esc(p.name)}</strong><br><span>${esc(p.ownerName||p.pluginId)} · ${esc(p.targetName||p.targetId)}</span><br><code>${esc(p.pluginId)}/${esc(p.targetId)}</code><br><small>${p.templateId?'插件默认计划':'用户计划'}</small></td><td>${esc(planStatus(p))}</td><td>${p.scheduleType==='interval'?`每 ${p.intervalMinutes} 分钟`:esc(p.cronExpression)}<br>${esc(p.timeZoneId)}<br>下次：${date(p.nextRunAtUtc)}</td><td>${esc(runLabel(p.lastRunStatus))}<br><small>${esc(p.misfireSummary||'')}</small></td><td><div class="actions">${['edit','run','history',p.isEnabled?'disable':'enable','delete'].map(a=>`<button class="button" data-plan-action="${a}" data-id="${p.id}">${({edit:'编辑',run:'立即执行',history:'运行记录',disable:'停用',enable:'启用',delete:'删除'})[a]}</button>`).join('')}</div></td></tr>`).join('')||'<tr><td colspan="5">暂无计划。</td></tr>'}</tbody></table></div><div class="pagination"><button class="button" id="scheduler-prev" ${data.page<=1?'disabled':''}>上一页</button><span>第 ${data.page} 页 · 共 ${data.totalCount} 项</span><button class="button" id="scheduler-next" ${data.page*data.pageSize>=data.totalCount?'disabled':''}>下一页</button></div>`;
  $('#scheduler-plugin').onchange=e=>{schedulerPlugin=e.target.value;schedulerPage=1;run(loadScheduler);};
  $('#scheduler-status').onchange=e=>{schedulerStatus=e.target.value;schedulerPage=1;run(loadScheduler);};
  $('#scheduler-refresh').onclick=()=>run(loadScheduler);
  $('#scheduler-create').onclick=()=>run(()=>openSchedulerEditor());
  $('#scheduler-prev').onclick=()=>{schedulerPage--;run(loadScheduler);};$('#scheduler-next').onclick=()=>{schedulerPage++;run(loadScheduler);};
  $('#scheduler').querySelectorAll('[data-plan-action]').forEach(b=>b.onclick=()=>run(()=>schedulerAction(b.dataset.planAction,b.dataset.id)));
}
async function schedulerAction(action,id){
  const plan=schedulerTasks.find(p=>p.id===id);
  if(action==='edit')return openSchedulerEditor(plan);
  if(action==='history'){taskRunsPlugin=plan.pluginId;taskRunsPlan=plan.id;taskRunsPage=1;runsCorrelation='';recordsSection='runs';switchTab('records');return;}
  if(action==='delete'&&!confirm(`删除“${plan.name}”？默认计划不会自动重新生成，运行历史与业务数据将保留。`))return;
  if(action==='run'){
    const result=await json(`/api/admin/scheduled-tasks/${id}/run`,{method:'POST'});
    flash('已提交执行，可在运行记录中查看。');return openTaskRun(result.runId);
  }
  await json(`/api/admin/scheduled-tasks/${id}${action==='delete'?'':`/${action}`}`,{method:action==='delete'?'DELETE':'POST'});await loadScheduler();
}
async function openSchedulerEditor(plan=null){
  schedulerEditing=plan;runDetailId=null;
  const [plugins, catalog]=await Promise.all([json('/api/admin/plugins'),json('/api/admin/tools')]);
  const definitions=await Promise.all(plugins.filter(p=>p.available).map(async p=>({plugin:p,definitions:await json(`/api/admin/plugins/${p.id}/task-definitions`)})));
  schedulerTargets=[];
  definitions.forEach(({plugin,definitions})=>(definitions.tasks||[]).forEach(t=>schedulerTargets.push({pluginId:plugin.id,targetType:'handler',targetId:t.id,name:t.name,ownerName:plugin.name,schema:t.parameterSchema,idempotent:t.idempotent,timeout:t.defaultTimeoutSeconds,risky:false})));
  catalog.categories.flatMap(c=>c.tools).filter(t=>t.isAvailable&&t.schedulable).forEach(t=>schedulerTargets.push({ownerId:t.pluginId,targetType:'tool',targetId:t.name,name:t.title||t.name,ownerName:t.ownerName||t.pluginId,schema:t.inputSchema,idempotent:t.idempotent,timeout:300,risky:t.requiresRiskConfirmation}));
  if(!plan&&schedulerPlugin)schedulerTargets.sort((a,b)=>Number(b.pluginId===schedulerPlugin)-Number(a.pluginId===schedulerPlugin));
  if(plan)schedulerTargets=schedulerTargets.filter(t=>t.pluginId===plan.pluginId&&t.targetType===plan.targetType&&t.targetId===plan.targetId);
  if(!schedulerTargets.length)throw new Error('当前没有可用目标；请先启用插件与工具。');
  $('#scheduler-dialog-title').textContent=plan?'编辑计划':'新建计划';
  $('#scheduler-dialog-content').innerHTML=`<form id="scheduler-form" class="scheduler-form"><label class="field"><span>名称</span><input name="name" maxlength="120" value="${esc(plan?.name||'')}" required></label><label class="field"><span>所属能力与执行目标</span><select name="target">${schedulerTargets.map((t,i)=>`<option value="${i}">${esc(t.ownerName||t.pluginId)} · ${esc(t.name)} (${t.targetType==='handler'?'任务处理器':'工具'})</option>`).join('')}</select></label><div id="scheduler-arguments" class="panel"></div><div class="field-grid"><label class="field"><span>调度方式</span><select name="scheduleType"><option value="interval" ${plan?.scheduleType!=='cron'?'selected':''}>固定周期</option><option value="cron" ${plan?.scheduleType==='cron'?'selected':''}>五段 Cron</option></select></label><label class="field"><span>时区</span><input name="timeZoneId" value="${esc(plan?.timeZoneId||'Asia/Shanghai')}" required></label><label class="field"><span>周期（分钟）</span><input name="intervalMinutes" type="number" min="1" max="10080" value="${plan?.intervalMinutes||15}"></label><label class="field"><span>Cron</span><input name="cronExpression" value="${esc(plan?.cronExpression||'0 9 * * *')}"></label><label class="field"><span>执行超时（秒）</span><input name="timeoutSeconds" type="number" min="1" max="86400" value="${plan?.timeoutSeconds||schedulerTargets[0].timeout}"></label><label class="field"><span>失败重试次数</span><select name="maxRetries">${[0,1,2].map(n=>`<option value="${n}" ${plan?.maxRetries===n?'selected':''}>${n}</option>`).join('')}</select></label></div><label class="field"><span><input name="isEnabled" type="checkbox" ${plan?.isEnabled!==false?'checked':''}> 启用计划</span></label><label id="scheduler-risk" class="field hidden"><span><input name="confirmRisk" type="checkbox" ${plan?.riskConfirmed?'checked':''}> 确认允许此工具无人值守执行</span></label><p id="scheduler-policy" class="muted"></p><div class="actions"><button class="button" type="button" id="scheduler-preview">校验与预览</button><button class="button primary">保存计划</button></div><p id="scheduler-preview-result" class="muted"></p></form>`;
  const form=$('#scheduler-form');
  form.elements.target.onchange=()=>{renderSchedulerArguments();form.elements.timeoutSeconds.value=schedulerTargets[Number(form.elements.target.value)].timeout;};
  renderSchedulerArguments();
  $('#scheduler-preview').onclick=()=>run(async()=>{const result=await json(`/api/admin/scheduled-tasks/preview${schedulerEditing?'?planId='+schedulerEditing.id:''}`,{method:'POST',body:JSON.stringify(schedulerRequest())});$('#scheduler-preview-result').textContent=result.nextOccurrencesUtc.map(date).join(' · ');});
  form.onsubmit=e=>{e.preventDefault();run(async()=>{await json(`/api/admin/scheduled-tasks${plan?'/'+plan.id:''}`,{method:plan?'PUT':'POST',body:JSON.stringify(schedulerRequest())});schedulerDialog.close();flash('计划已保存。');await loadScheduler();});};
  schedulerDialog.showModal();
}
function renderSchedulerArguments(){
  const form=$('#scheduler-form'),target=schedulerTargets[Number(form.elements.target.value)];
  const values=schedulerEditing?.arguments||{};
  const props=Object.entries(target.schema.properties||{}).map(([key,s])=>[key,schedulerParameterSchema(s)]);
  $('#scheduler-arguments').innerHTML=`<h3>执行参数</h3><div id="scheduler-fields" class="field-grid">${props.map(([key,s])=>`<label class="field"><span>${esc(key)}${target.schema.required?.includes(key)?' *':''}</span>${s.type==='array'||s.type==='object'?`<textarea data-argument="${esc(key)}" rows="3">${esc(JSON.stringify(values[key]??s.default??(s.type==='array'?[]:{})))}</textarea>`:`<input data-argument="${esc(key)}" value="${esc(values[key]??s.default??'')}" placeholder="${esc(s.description||'')}">`}</label>`).join('')||'<p>无需参数。</p>'}</div><details><summary>原始 JSON 与参数定义</summary><label><input id="scheduler-use-json" type="checkbox"> 使用原始 JSON</label><textarea id="scheduler-json" rows="5">${esc(JSON.stringify(values,null,2))}</textarea><pre>${esc(JSON.stringify(target.schema,null,2))}</pre></details>`;
  $('#scheduler-risk').classList.toggle('hidden',!target.risky);
  form.elements.maxRetries.disabled=!target.idempotent;if(!target.idempotent)form.elements.maxRetries.value='0';
  $('#scheduler-policy').textContent=`${target.idempotent?'允许失败重试':'此目标未声明幂等，不允许自动重试'}；同一计划不会重叠执行。`;
}
function schedulerRequest(){
  const form=$('#scheduler-form'),t=schedulerTargets[Number(form.elements.target.value)],values={};
  if($('#scheduler-use-json').checked){const raw=JSON.parse($('#scheduler-json').value);if(!raw||Array.isArray(raw)||typeof raw!=='object')throw new Error('参数必须为 JSON 对象。');Object.assign(values,raw);}
  else for(const input of $('#scheduler-fields').querySelectorAll('[data-argument]')){
    const schema=schedulerParameterSchema(t.schema.properties[input.dataset.argument]),value=input.value.trim();if(value==='')continue;
    const type=Array.isArray(schema.type)?schema.type.find(x=>x!=='null'):schema.type;
    values[input.dataset.argument]=type==='array'||type==='object'||type==='boolean'?JSON.parse(value):type==='integer'||type==='number'?Number(value):value;
  }
  return {name:form.elements.name.value,ownerId:t.pluginId,targetType:t.targetType,targetId:t.targetId,arguments:values,
    scheduleType:form.elements.scheduleType.value,intervalMinutes:Number(form.elements.intervalMinutes.value),cronExpression:form.elements.cronExpression.value,
    timeZoneId:form.elements.timeZoneId.value,isEnabled:form.elements.isEnabled.checked,timeoutSeconds:Number(form.elements.timeoutSeconds.value),maxRetries:Number(form.elements.maxRetries.value),confirmRisk:form.elements.confirmRisk.checked};
}
async function loadTaskRuns(){
  const generation=++runsGeneration;
  const query=new URLSearchParams({page:String(taskRunsPage),pageSize:'20'});
  if(taskRunsPlugin)query.set('ownerId',taskRunsPlugin);if(taskRunsPlan)query.set('taskId',taskRunsPlan);
  for(const [key,value] of [['status',runsStatus],['trigger',runsTrigger],['correlationId',runsCorrelation]])if(value)query.set(key,value);
  if(runsFrom)query.set('fromUtc',new Date(runsFrom).toISOString());if(runsTo)query.set('toUtc',new Date(runsTo).toISOString());
  const [data,owners,plan]=await Promise.all([json(`/api/admin/task-runs?${query}`),json('/api/admin/capability-owners'),taskRunsPlan?json(`/api/admin/scheduled-tasks/${taskRunsPlan}`).catch(()=>null):null]);
  if(generation!==runsGeneration||tab!=='records'||recordsSection!=='runs')return;
  const owner=owners.find(p=>p.id===taskRunsPlugin);
  const options=(values,selected)=>values.map(([v,l])=>`<option value="${esc(v)}" ${v===selected?'selected':''}>${esc(l)}</option>`).join('');
  $('#runs').innerHTML=`<div class="record-subhead"><h2>任务运行</h2><button class="button" id="runs-refresh">刷新</button></div>${taskRunsPlugin||taskRunsPlan||runsCorrelation?`<div class="context-banner"><span>当前范围：${esc(owner?.name||taskRunsPlugin||'全部')}${taskRunsPlan?' · '+esc(plan?.name||taskRunsPlan):''}${runsCorrelation?' · 关联执行':''}</span><button class="button" id="runs-context-clear">清除范围</button></div>`:''}
  <form id="runs-filter-form" class="panel"><div class="filters records-filters"><label>所属能力 / 插件<select id="runs-plugin"><option value="">全部</option>${owners.map(p=>`<option value="${esc(p.id)}" ${p.id===taskRunsPlugin?'selected':''}>${esc(p.name)}${p.kind==='module'?'（内置）':''}</option>`).join('')}</select></label><label>状态<select id="runs-status">${options([['','全部'],...['queued','running','succeeded','failed','cancelled','timed_out','interrupted','skipped'].map(s=>[s,runLabel(s)])],runsStatus)}</select></label><label>触发来源<select id="runs-trigger">${options([['','全部'],['manual','手动执行'],['scheduled','定时执行'],['plugin','插件请求']],runsTrigger)}</select></label><label>开始时间<input id="runs-from" type="datetime-local" value="${esc(runsFrom)}"></label><label>结束时间<input id="runs-to" type="datetime-local" value="${esc(runsTo)}"></label><label>计划范围<input readonly value="${esc(plan?.name||(taskRunsPlan?'历史计划':'全部计划'))}"></label></div><div class="actions"><button class="button primary" type="submit">筛选</button><button class="button" type="button" id="runs-clear">清除筛选</button></div></form>
  <div class="panel table-wrap"><table class="table records-table"><thead><tr><th>时间 / 耗时</th><th>所属能力 / 任务</th><th>状态 / 触发</th><th>结果摘要</th><th style="width:72px">操作</th></tr></thead><tbody>${data.items.map(r=>`<tr><td>${date(r.createdAtUtc)}<br><small class="muted">${r.durationMilliseconds!=null?r.durationMilliseconds+' 毫秒':'—'}</small></td><td><strong>${esc(r.taskName||r.targetName||r.targetId)}</strong><br>${esc(r.ownerName||r.pluginId)} · ${esc(r.targetName||r.targetId)}<br><code>${esc(r.pluginId)}/${esc(r.targetId)}</code></td><td><span class="pill ${r.status==='succeeded'?'green':['failed','timed_out'].includes(r.status)?'red':'amber'}">${esc(runLabel(r.status))}</span><br><small>${esc(triggerLabel(r.trigger))} · 尝试 ${r.attempt}${r.cancelRequested&&r.status==='running'?' · 已请求取消':''}</small></td><td class="summary-cell"><span class="summary-text" title="${esc(r.summary||r.error||'')}">${esc(r.summary||r.error||'—')}</span></td><td><button class="button" data-run="${r.id}">详情</button></td></tr>`).join('')||'<tr><td colspan="5" class="empty">当前筛选条件下暂无记录。</td></tr>'}</tbody></table></div>${pagination(data,'runs')}`;
  $('#runs-filter-form').onsubmit=e=>{e.preventDefault();const nextOwner=$('#runs-plugin').value;if(nextOwner!==taskRunsPlugin)taskRunsPlan='';taskRunsPlugin=nextOwner;runsStatus=$('#runs-status').value;runsTrigger=$('#runs-trigger').value;runsFrom=$('#runs-from').value;runsTo=$('#runs-to').value;if(runsFrom&&runsTo&&runsFrom>runsTo){flash('开始时间不能晚于结束时间。',true);return;}taskRunsPage=1;run(loadTaskRuns);};
  const clear=()=>{taskRunsPage=1;taskRunsPlugin='';taskRunsPlan='';runsStatus='';runsTrigger='';runsFrom='';runsTo='';runsCorrelation='';run(loadTaskRuns);};
  $('#runs-clear').onclick=clear;if($('#runs-context-clear'))$('#runs-context-clear').onclick=clear;
  $('#runs-refresh').onclick=()=>run(loadTaskRuns);
  $('#runs').querySelectorAll('[data-page]').forEach(b=>b.onclick=()=>{taskRunsPage=Number(b.dataset.page);run(loadTaskRuns);});
  $('#runs').querySelectorAll('[data-run]').forEach(b=>b.onclick=()=>run(()=>openTaskRun(b.dataset.run)));
}
function resultSummary(value){
  if(value==null)return '<p class="muted">暂无结果。</p>';
  if(typeof value!=='object')return `<p>${esc(value)}</p>`;
  const rows=Array.isArray(value)?value:Object.entries(value).map(([key,v])=>({字段:key,值:typeof v==='object'?JSON.stringify(v):v}));
  if(!rows.length)return '<p class="muted">结果为空。</p>';
  if(rows.every(r=>r&&typeof r==='object'&&!Array.isArray(r))){
    const keys=[...new Set(rows.flatMap(r=>Object.keys(r)))].slice(0,6);
    return `<div class="table-wrap"><table class="table"><thead><tr>${keys.map(k=>`<th>${esc(k)}</th>`).join('')}</tr></thead><tbody>${rows.slice(0,20).map(r=>`<tr>${keys.map(k=>`<td>${esc(typeof r[k]==='object'?JSON.stringify(r[k]):r[k]??'—')}</td>`).join('')}</tr>`).join('')}</tbody></table></div>${rows.length>20?'<p class="muted">显示前 20 项，完整内容见原始结果。</p>':''}`;
  }
  return `<pre class="run-result">${esc(JSON.stringify(value,null,2))}</pre>`;
}
function payloadBlock(id,title,value){return `<details class="payload-block" data-payload="${id}"><summary>${esc(title)}</summary><div class="payload-heading"><small class="muted">原始数据</small><button class="button" data-copy-payload="${id}">复制</button></div><pre tabindex="0" id="payload-${id}">${esc(JSON.stringify(value,null,2))}</pre></details>`;}
async function openTaskRun(id){
  const changed=runDetailId!==id;runDetailId=id;const generation=++runDetailGeneration;runDetailLoading=true;
  if(changed){$('#scheduler-dialog-title').textContent='执行详情';$('#scheduler-dialog-content').innerHTML='<p role="status">正在加载执行详情…</p>';}
  if(!schedulerDialog.open)schedulerDialog.showModal();
  try{
    const r=await json(`/api/admin/task-runs/${id}`);if(runDetailId!==id||generation!==runDetailGeneration||!schedulerDialog.open)return;
    const content=$('#scheduler-dialog-content'),scroll=schedulerDialog.scrollTop,opened=[...content.querySelectorAll('details[open]')].map(d=>d.dataset.payload),focused=document.activeElement?.id;
    $('#scheduler-dialog-title').textContent=`${r.taskName||r.targetName||r.targetId} · 执行详情`;
    content.innerHTML=`<dl class="run-metadata">${[['所属能力 / 插件',r.ownerName||r.pluginId],['执行目标',r.targetName||r.targetId],['计划',r.taskName||r.taskId],['状态',runLabel(r.status)+(r.cancelRequested&&r.status==='running'?' · 已请求取消，等待执行结束':'')],['触发来源',triggerLabel(r.trigger)],['尝试次数',r.attempt],['开始时间',date(r.startedAtUtc)],['结束时间',date(r.completedAtUtc)],['耗时',r.durationMilliseconds!=null?r.durationMilliseconds+' 毫秒':'—'],['下次重试',date(r.retryAtUtc)]].map(([k,v])=>`<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join('')}</dl><section class="panel"><h3>结果摘要</h3><p>${esc(r.summary||'暂无摘要。')}</p>${resultSummary(r.result)}</section>${r.error?`<div class="notice warning"><strong>执行错误</strong><p>${esc(r.error)}</p></div>`:''}${r.artifact?`<section class="panel"><h3>产物引用</h3><p>${esc(r.artifact.name||r.artifact.id)}</p><small>${esc(r.artifact.mimeType)} · ${esc(r.artifact.id)}</small></section>`:''}${payloadBlock('arguments','参数快照',r.arguments)}${payloadBlock('result','原始结果',r.result)}${r.artifact?payloadBlock('artifact','原始产物引用',r.artifact):''}${payloadBlock('metadata','执行标识与版本',{runId:r.id,ownerId:r.ownerId,targetId:r.targetId,ownerVersion:r.pluginVersion,definitionVersion:r.definitionVersion,correlationId:r.correlationId})}<div class="actions"><button class="button" id="run-refresh">刷新</button>${['queued','running'].includes(r.status)?'<button class="button danger" id="run-cancel">取消执行</button>':''}${r.invocationLogId?'<button class="button" id="run-log">关联调用日志</button>':''}</div><p id="run-detail-message" class="muted" role="status"></p>`;
    for(const key of opened)content.querySelector(`[data-payload="${key}"]`)?.setAttribute('open','');
    content.querySelectorAll('[data-copy-payload]').forEach(b=>b.onclick=()=>run(async()=>{await navigator.clipboard.writeText($(`#payload-${b.dataset.copyPayload}`).textContent);$('#run-detail-message').textContent='已复制。';}));
    $('#run-refresh').onclick=()=>run(()=>openTaskRun(id));
    if($('#run-cancel'))$('#run-cancel').onclick=()=>run(async()=>{await json(`/api/admin/task-runs/${id}/cancel`,{method:'POST'});await openTaskRun(id);});
    if($('#run-log'))$('#run-log').onclick=()=>{schedulerDialog.close();runDetailId=null;logCorrelation=r.correlationId;logPage=1;switchTab('logs');run(()=>openLogDetail(r.invocationLogId));};
    if(!changed){if(focused)document.getElementById(focused)?.focus({preventScroll:true});schedulerDialog.scrollTop=scroll;}
    runDetailActive=['queued','running'].includes(r.status);
  }catch(error){if(generation===runDetailGeneration&&schedulerDialog.open){if(changed)$('#scheduler-dialog-content').innerHTML=`<p class="error-text">详情加载失败：${esc(error.message)}</p><button class="button" id="run-retry">重试</button>`;if($('#run-retry'))$('#run-retry').onclick=()=>run(()=>openTaskRun(id));else if($('#run-detail-message'))$('#run-detail-message').textContent='刷新失败：'+error.message;}}
  finally{if(generation===runDetailGeneration)runDetailLoading=false;}
}
setInterval(()=>{if(runDetailId&&runDetailActive&&schedulerDialog.open&&!runDetailLoading)run(()=>openTaskRun(runDetailId));},2000);
schedulerDialog.addEventListener('close',()=>{if(!schedulerDialog.open){runDetailId=null;runDetailGeneration++;runDetailLoading=false;}});
