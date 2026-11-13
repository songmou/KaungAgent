let contentFile = null, contentJobId = '', contentPoll = null, contentGeneration = 0;
const contentTerminal = status => !['queued', 'running'].includes(status);
async function contentTool(name, args) {
  const response = await json(`/api/admin/tools/${name}/test`, {method:'POST',body:JSON.stringify({arguments:args,confirmRisk:true})});
  if (!response.succeeded) throw new Error(response.error || response.result.content?.find(c=>c.type==='text')?.text || '调用失败。');
  if (response.result.structuredContent) return response.result.structuredContent;
  return JSON.parse(response.result.content.filter(c=>c.type==='text').map(c=>c.text).join('\n'));
}
async function loadContent() {
  clearTimeout(contentPoll);
  const generation = ++contentGeneration;
  const [plugins, runs] = await Promise.all([json('/api/admin/plugins'), json('/api/admin/task-runs?ownerId=content&pageSize=20')]);
  if (tab !== 'content' || generation !== contentGeneration) return;
  const plugin = plugins.find(p=>p.id==='content');
  $('#content').innerHTML = `<div class="page-head"><div><h1>内容处理</h1><p>上传文件或导入公开下载链接，进行 OCR、文档解析及音频转写。</p></div><button class="button" id="content-refresh">刷新</button></div>
    <div class="panel"><h2>引擎状态</h2>${plugin ? `<p>${esc(pluginState(plugin))}</p>${(plugin.health||[]).map(h=>`<p>${pluginHealthBadge(h)} ${esc(h.detail)}</p>`).join('')}` : '<p>内容插件未安装，请安装后重启。</p>'}<button class="button" id="content-config">插件配置</button></div>
    <form id="content-upload" class="panel"><h2>1. 准备文件</h2><label>上传文件<input type="file" id="content-file" accept=".pdf,.doc,.docx,.xls,.xlsx,.txt,.md,.csv,.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp,.wav,.mp3,.m4a,.aac,.ogg,.flac"></label><div class="actions"><button class="button primary" type="submit">上传</button></div><label>公开下载链接<input type="url" id="content-url" placeholder="https://example.com/document.pdf"></label><div class="actions"><button class="button" type="button" id="content-import">导入链接</button></div><div id="content-file-info"></div></form>
    <form id="content-process" class="panel"><h2>2. 选择处理方式</h2><div class="content-fields"><label>处理方式<select id="content-operation"><option value="document">文档解析</option><option value="ocr">OCR 文字识别</option><option value="speech">音频转写</option></select></label><label data-content-kind="document ocr">PDF 页范围<input id="content-pages" placeholder="留空处理全部，或填写 1-3,5"></label><label data-content-kind="document">扫描文字识别<select id="content-ocr"><option value="auto">自动</option><option value="force">强制 OCR</option><option value="off">关闭 OCR</option></select></label><label data-content-kind="document">Excel 工作表<input id="content-sheets" placeholder="留空处理全部；多个名称用逗号分隔"></label><label data-content-kind="document">文本编码<select id="content-encoding"><option value="auto">自动识别</option><option value="utf-8">UTF-8</option><option value="utf-16">UTF-16（含 BOM）</option><option value="gb18030">GB18030</option></select></label><label data-content-kind="speech">音频语言<select id="content-language"><option value="auto">自动识别</option><option value="zh">中文</option><option value="en">英文</option></select></label><label data-content-kind="speech"><input type="checkbox" id="content-words">包含词级时间戳</label></div><div class="actions"><button type="submit" class="button primary" ${plugin?.available?'':'disabled'}>提交处理</button></div><p id="content-submit-state" role="status"></p></form>
    <section id="content-job" class="panel" aria-live="polite"></section><section id="content-preview" class="panel hidden"></section>
    <div class="panel"><h2>最近处理任务</h2><div class="actions" id="content-recent-list">${runs.items.filter(r=>!r.taskId).map(r=>`<button class="button" data-content-job="${esc(r.id)}">${esc(r.targetName||r.targetId)} · ${esc(runLabel(r.status))} · ${date(r.createdAtUtc)}</button>`).join('')||'<p class="muted">暂无处理记录。完整历史请查看执行记录。</p>'}</div><button class="button" id="content-records">执行记录</button></div>`;
  $('#content-refresh').onclick = ()=>run(loadContent);
  $('#content-config').onclick = ()=>{selectedPluginId='content';pluginSection='configuration';switchTab('plugins');};
  $('#content-records').onclick = ()=>{taskRunsPlugin='content';taskRunsPlan='';switchTab('records');};
  $('#content-upload').onsubmit = event=>{event.preventDefault();run(async()=>{
    const file=$('#content-file').files[0];if(!file)throw new Error('请先选择文件。');
    const button=event.submitter;button.disabled=true;
    try { const data=new FormData();data.append('file',file);contentFile=await json('/api/admin/files',{method:'POST',body:data});renderContentFile(); }
    finally {button.disabled=false;}
  });};
  $('#content-import').onclick = ()=>run(async()=>{const button=$('#content-import');button.disabled=true;try{contentFile=await contentTool('kaung_file_import',{url:$('#content-url').value});renderContentFile();}finally{button.disabled=false;}});
  $('#content-operation').onchange = renderContentOptions;
  $('#content-process').onsubmit = event=>{event.preventDefault();run(async()=>{
    if(!contentFile)throw new Error('请先上传或导入文件。');
    const operation=$('#content-operation').value;
    const args={fileId:contentFile.fileId};
    if(operation!=='speech')args.pages=$('#content-pages').value.trim();
    if(operation==='document')Object.assign(args,{ocrMode:$('#content-ocr').value,sheetNames:$('#content-sheets').value.split(/[,，]/).map(s=>s.trim()).filter(Boolean),encoding:$('#content-encoding').value});
    if(operation==='speech')Object.assign(args,{language:$('#content-language').value,wordTimestamps:$('#content-words').checked});
    const button=event.submitter;button.disabled=true;
    try {const result=await contentTool({document:'kaung_document_parse',ocr:'kaung_ocr_recognize',speech:'kaung_audio_transcribe'}[operation],args);contentJobId=result.jobId;$('#content-submit-state').textContent='已提交任务，可离开页面后从执行记录查看。';await pollContentJob();}
    finally {button.disabled=false;}
  });};
  $('#content').querySelectorAll('[data-content-job]').forEach(button=>button.onclick=()=>{contentJobId=button.dataset.contentJob;run(pollContentJob);});
  renderContentOptions();renderContentFile();
  if(contentJobId)await pollContentJob();else $('#content-job').innerHTML='<h2>3. 处理结果</h2><p class="muted">提交任务后将在这里显示状态和结果。</p>';
}
function renderContentOptions(){const kind=$('#content-operation').value;$('#content').querySelectorAll('[data-content-kind]').forEach(e=>e.classList.toggle('hidden',!e.dataset.contentKind.split(' ').includes(kind)));}
function renderContentFile(){
  $('#content-file-info').innerHTML=contentFile?`<p><strong>${esc(contentFile.name)}</strong> · ${(contentFile.size/1024).toFixed(1)} KiB · 有效期至 ${date(contentFile.expiresAtUtc)}</p><code>${esc(contentFile.fileId)}</code><div class="actions"><button type="button" class="button danger" id="content-file-delete">删除文件</button></div>`:'<p class="muted">尚未选择文件。</p>';
  if(contentFile)$('#content-file-delete').onclick=()=>run(async()=>{await api(`/api/admin/files/${contentFile.fileId}`,{method:'DELETE'});contentFile=null;renderContentFile();});
}
async function pollContentJob(){
  clearTimeout(contentPoll);
  const id=contentJobId, generation=contentGeneration;
  if(tab!=='content'||!id||!adminKey)return;
  const job=await json(`/api/admin/content/jobs/${id}`);
  if(tab!=='content'||id!==contentJobId||generation!==contentGeneration)return;
  const files=job.result?.files||[];
  $('#content-job').innerHTML=`<h2>3. 处理结果</h2><p><span class="pill ${job.status==='succeeded'?'green':'amber'}">${esc(runLabel(job.status))}</span> ${job.cancelRequested?'已请求取消':''}</p><code>${esc(id)}</code><p>${esc(job.summary||job.error||'等待处理完成…')}</p>${(job.result?.warnings||[]).map(w=>`<p class="notice warning">${esc(w)}</p>`).join('')}${files.map(f=>`<div class="content-output"><strong>${esc(f.name)}</strong><small>${esc(f.mimeType)} · ${(f.size/1024).toFixed(1)} KiB</small><div class="actions"><button class="button" data-content-preview="${esc(f.fileId)}">预览</button><button class="button" data-content-download="${esc(f.fileId)}" data-name="${esc(f.name)}">下载</button></div></div>`).join('')}${contentTerminal(job.status)?'':'<button class="button danger" id="content-cancel">取消任务</button>'}`;
  if(!contentTerminal(job.status)){
    $('#content-cancel').onclick=()=>run(async()=>{await json(`/api/admin/content/jobs/${id}/cancel`,{method:'POST'});await pollContentJob();});
    contentPoll=setTimeout(()=>run(pollContentJob),1500);
  }
  if(contentTerminal(job.status)) refreshContentHistory(generation).catch(()=>{});
  $('#content-job').querySelectorAll('[data-content-preview]').forEach(b=>b.onclick=()=>run(()=>previewContent(b.dataset.contentPreview,0,false)));
  $('#content-job').querySelectorAll('[data-content-download]').forEach(b=>b.onclick=()=>run(async()=>{
    const response=await api(`/api/admin/files/${b.dataset.contentDownload}/download`);const url=URL.createObjectURL(await response.blob());const link=document.createElement('a');link.href=url;link.download=b.dataset.name;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  }));
}
async function previewContent(id,start,append){
  const generation=contentGeneration;
  const page=await json(`/api/admin/files/${id}/text?startIndex=${start}&maxCharacters=8000`);
  if(tab!=='content'||generation!==contentGeneration)return;
  const panel=$('#content-preview');
  if(!append)panel.innerHTML='<h2>结果预览</h2><pre class="content-text"></pre><p class="muted" id="content-preview-count"></p><button class="button" id="content-preview-next">继续读取</button>';
  panel.classList.remove('hidden');panel.querySelector('pre').textContent+=page.content;
  $('#content-preview-count').textContent=`已读取 ${page.startIndex+page.returnedCharacters} / ${page.totalCharacters} 字符`;
  $('#content-preview-next').classList.toggle('hidden',page.nextStartIndex==null);
  $('#content-preview-next').onclick=()=>run(()=>previewContent(id,page.nextStartIndex,true));
}

async function refreshContentHistory(generation){
  const runs=await json('/api/admin/task-runs?ownerId=content&pageSize=20');
  if(tab!=='content'||generation!==contentGeneration)return;
  const list=$('#content-recent-list');
  list.innerHTML=runs.items.filter(r=>!r.taskId).map(r=>`<button class="button" data-content-job="${esc(r.id)}">${esc(r.targetName||r.targetId)} · ${esc(runLabel(r.status))} · ${date(r.createdAtUtc)}</button>`).join('')||'<p class="muted">暂无处理记录。完整历史请查看执行记录。</p>';
  list.querySelectorAll('[data-content-job]').forEach(button=>button.onclick=()=>{contentJobId=button.dataset.contentJob;run(pollContentJob);});
}
