'use strict';

// A deterministic interaction prototype. This file never contacts a model or executes code.
const agents = [
  {id:'openai',name:'GPT',role:'Project lead',runtime:'Model API',model:'Select an available GPT model',scope:'Plan work, resolve handoffs, and review the integrated result.'},
  {id:'claude',name:'Claude',role:'Implementation',runtime:'Coding-agent adapter',model:'Select an available Claude model',scope:'Implement a scoped change in an isolated task workspace.'},
  {id:'gemini',name:'Gemini',role:'Research',runtime:'Model API',model:'Select an available Gemini model',scope:'Gather source evidence and compare requirements.'},
  {id:'deepseek',name:'DeepSeek',role:'API & contracts',runtime:'Model API',model:'Select an available DeepSeek model',scope:'Propose interfaces and validate edge cases against the brief.'},
  {id:'kimi',name:'Kimi',role:'Visual exploration',runtime:'Model API',model:'Select an available Kimi model',scope:'Explore visual alternatives using explicitly configured tools. The Kimi web app’s swarm and 3D features are not included by an API key.'},
  {id:'ollama',name:'Local model',role:'Test assistant',runtime:'Ollama',model:'Choose an installed local model',scope:'Suggest tests and summarize results. Actual tests must run in a separate controlled runner.'},
];
const initialTasks = [
  {id:'LM-01',title:'Define the acceptance contract',owner:'openai',state:'done',progress:100,deps:[],kind:'Plan',note:'Scope signed off',file:'brief.md',checks:['The user goal has explicit acceptance criteria.','Task boundaries and file ownership are recorded.','No deployment or publication is authorized.'],output:'# Atlas dashboard\n\nShip a keyboard-accessible project dashboard.\nInclude a project list, status filters and a detail view.\nDo not add billing, authentication or deployment in this run.'},
  {id:'LM-02',title:'Research accessible dashboard patterns',owner:'gemini',state:'running',progress:62,deps:['LM-01'],kind:'Research',note:'Comparing navigation patterns',file:'research-notes.md',checks:['Each recommendation links to a source.','Keyboard navigation and empty states are covered.','Findings stay within the dashboard brief.'],output:'# Research handoff\n\nUse native buttons for task cards.\nPreserve focus when a detail panel closes.\nInclude a text equivalent for the dependency graph.\n\nFixture only: these findings are scripted, not live research.'},
  {id:'LM-03',title:'Build the dashboard shell',owner:'claude',state:'running',progress:38,deps:['LM-01'],kind:'Build',note:'Implementing keyboard navigation',file:'dashboard.patch',checks:['Keyboard focus reaches every task.','Selected filters survive opening a detail panel.','Responsive layout works at 390px and 1440px.'],output:'--- dashboard.tsx\n+++ dashboard.tsx\n+ <button onClick={() => openTask(task.id)}>\n+   <span>{task.title}</span>\n+   <span>{task.status}</span>\n+ </button>\n\nIllustrative patch. No repository files were modified.'},
  {id:'LM-04',title:'Agree the task-state API',owner:'deepseek',state:'review',progress:100,deps:['LM-01'],kind:'Contract',note:'Ready for your review',file:'task-contract.json',checks:['Updates include a task ID and expected revision.','Completed tasks include evidence references.','Duplicate transition requests are idempotent.'],output:'{\n  "task_id": "LM-03",\n  "expected_revision": 4,\n  "transition": "submit_for_review",\n  "evidence": ["test-run-42", "patch-sha256"]\n}\n\nExample contract only, not a running endpoint.'},
  {id:'LM-05',title:'Explore visual hierarchy',owner:'kimi',state:'queued',progress:0,deps:['LM-02','LM-03'],kind:'Design',note:'Waiting for research + shell',file:'visual-direction.md',checks:['Labels are readable in both themes.','Status is conveyed by words as well as color.','Motion respects the user’s reduced-motion preference.'],output:'# Visual direction\n\nA quiet graphite workspace with a warm green action color.\nKeep the project board central and the activity feed secondary.\nUse provider marks to identify agents, not rank their intelligence.'},
  {id:'LM-06',title:'Check the interaction contract',owner:'ollama',state:'queued',progress:0,deps:['LM-03','LM-04'],kind:'Test',note:'Waiting for shell + API contract',file:'test-report.txt',checks:['Focus returns after a dialog closes.','Blocked tasks cannot start early.','Failed checks keep the task out of Done.'],output:'SIMULATED TEST REPORT\n\nKeyboard navigation: PASS (fixture)\nDialog focus restore: PASS (fixture)\nDependency guard: PASS (fixture)\n\nThis is sample evidence, not an actual test execution.'},
  {id:'LM-07',title:'Review the integrated handoff',owner:'openai',state:'queued',progress:0,deps:['LM-02','LM-05','LM-06'],kind:'Review',note:'Waiting for design + checks',file:'handoff.md',checks:['The combined result satisfies the original brief.','A separate review records unresolved findings.','The human accepts the final revision before anything ships.'],output:'# Ready for human review\n\nScope: project dashboard only.\nEvidence: research, patch, API contract, visual review, tests.\nRemaining: run the real integration suite on a pinned revision.\n\nNo code has been published by this demo.'},
];
const $ = (s,root=document) => root.querySelector(s);
const esc = value => String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const agent = id => agents.find(a=>a.id===id);
const avatar = (id,mini=false) => `<span class="avatar ${esc(id)} ${mini?'mini':''}"><img src="assets/${esc(id)}.svg" alt="${esc(agent(id)?.name||'Agent')} mark"></span>`;
const states = {queued:'Queued',running:'In progress',review:'Needs review',done:'Accepted'};
let tasks,events,spend,running=false,timer=null,currentView='room',tick=0,noticeTimer,returnFocus;
const taskById = id => tasks.find(t=>t.id===id);
const missing = t => t.deps.filter(id=>taskById(id)?.state!=='done');
const available = t => t.state==='queued' && missing(t).length===0;

function init(){
  stop();tasks=structuredClone(initialTasks);spend=.42;tick=0;
  events=[
    {who:'openai',to:'Team',text:'Brief v1 is approved. Claude owns the shell; Gemini owns research. Use the same acceptance contract.',tag:'LM-01 · accepted',time:'00:00'},
    {who:'deepseek',to:'GPT',text:'The task-state contract is ready. I added revision checks so two agents cannot overwrite the same transition.',tag:'LM-04 · review requested',time:'00:09'},
    {who:'claude',to:'Kimi',text:'I’m building the shell. I’ll send the component structure after it passes review, so you can work against a stable version.',tag:'LM-03 → LM-05 · handoff pending',time:'00:14'},
    {who:'ollama',to:'DeepSeek',text:'Waiting for the accepted contract and shell. I won’t start the interaction checks against an unfinished interface.',tag:'LM-06 · dependency wait',time:'00:18'},
  ];render();
}
function emit(who,to,text,tag){tick++;events.push({who,to,text,tag,time:`00:${String(18+tick).padStart(2,'0')}`});}
function toast(text){clearTimeout(noticeTimer);$('#toast').textContent=text;$('#toast').classList.add('visible');noticeTimer=setTimeout(()=>$('#toast').classList.remove('visible'),4200);}
function stop(){running=false;clearInterval(timer);timer=null;}
function start(){
  if(tasks.every(t=>t.state==='done')){toast('The sample mission is complete. Reset to replay.');return;}
  if(spend>=5){toast('Demo budget reached. Reset the run to replay.');return;}
  if(!tasks.some(t=>t.state==='running'||available(t))){toast('Review the submitted work to unlock the next tasks.');return;}
  running=true;timer=setInterval(step,2000);render();
}
function step(){
  const next=tasks.find(available);
  if(next && !tasks.some(t=>t.state==='running'&&t.owner===next.owner)){
    next.state='running';next.note='Reading the accepted handoff';next.progress=8;
    emit(next.owner,'Team',`${next.id} is unblocked. I have the accepted inputs and am starting in my own task workspace.`,`${next.id} · started`);
  }
  const work=tasks.filter(t=>t.state==='running');
  for(const t of work){
    if(spend+.07>5){stop();toast('Demo spending cap reached. No further steps will start.');break;}
    spend=Number((spend+.07).toFixed(2));t.progress=Math.min(100,t.progress+21);
    if(t.progress===100){t.state='review';t.note='Evidence attached · awaiting acceptance';emit(t.owner,'GPT',`${t.title} is ready for review. I attached ${t.file} and the acceptance checklist. Please review before downstream tasks start.`,`${t.id} · evidence ready`);}
    else{t.note=t.progress>70?'Checking acceptance criteria':'Working in an isolated branch';emit(t.owner,'Team',`${t.id}: ${t.progress>70?'checking the acceptance criteria before submission.':'working within the assigned scope; no shared-branch edits.'}`,`${t.id} · progress update`);}
  }
  if(!tasks.some(t=>t.state==='running'||available(t))){stop();toast('The team is waiting for your review. Open a card in Needs review.');}
  render();
}
function agentSummary(id){
  const owned=tasks.filter(t=>t.owner===id);
  const t=owned.find(t=>t.state==='running')||owned.find(t=>t.state==='review')||owned.find(t=>t.state==='queued')||owned.at(-1);
  const state=!t?'Available':t.state==='queued'?(missing(t).length?'Waiting':'Ready'):t.state==='running'?(running?'Working':'Paused'):t.state==='review'?'In review':'Available';
  return {t,state};
}
function renderRoster(){
  $('#agents').innerHTML=agents.map(a=>{const {t,state}=agentSummary(a.id);return `<button class="agent-card" data-agent="${a.id}" aria-label="Inspect ${a.name}, ${state}"><header>${avatar(a.id)}<div><span class="agent-name">${a.name}</span><span class="agent-role">${a.role}</span></div></header><div class="agent-status ${state==='In review'?'review':['Waiting','Available','Paused'].includes(state)?'waiting':''}"><span class="dot"></span>${state}</div><div class="agent-task">${t?`${t.id} · ${esc(t.title)}`:'No assigned tasks'}</div><i class="progress" style="width:${t?.state==='running'?t.progress:0}%"></i></button>`;}).join('');
}
function render(){
  const done=tasks.filter(t=>t.state==='done').length;
  $('#play').textContent=running?'Ⅱ Pause demo':'▶ Run demo';
  $('#run-status').textContent=tasks.every(t=>t.state==='done')?'Mission accepted':running?'Demo running':tasks.some(t=>t.state==='running')?'Ready to replay':'Waiting for review';
  $('#run-dot').classList.toggle('amber',!running);
  $('#mission-count').textContent=`${done} / ${tasks.length} accepted`;
  $('#side-complete').textContent=`${done} of ${tasks.length} tasks accepted`;
  $('#side-progress').style.width=`${done/tasks.length*100}%`;
  $('#nav-count').textContent=tasks.length;
  $('#spend').textContent=`$${spend.toFixed(2)}`;
  $('#lane-count').textContent=`${tasks.filter(t=>t.state==='running').length} ${running?'active':'paused'} · ${tasks.filter(t=>t.state==='queued').length} queued`;
  renderRoster();renderView();
}
function card(t){
  const blocked=missing(t);
  const note=t.state==='queued'&&blocked.length?`Waiting on ${blocked.join(', ')}`:t.note;
  return `<button class="task" data-task="${t.id}" aria-label="${t.id}: ${esc(t.title)}, ${states[t.state]}"><div class="task-id"><span>${t.id}</span><span>${t.state==='done'?'✓':t.state==='review'?'◉':'···'}</span></div><h3>${esc(t.title)}</h3><div class="task-note ${t.state==='queued'&&blocked.length?'blocked':''}">${esc(note)}</div><div class="task-foot"><span class="owner">${avatar(t.owner,true)}${agent(t.owner).name}</span><span class="task-tag">${esc(t.kind)}</span></div></button>`;
}
function board(){return `<div class="section-heading"><h2>Shared task board</h2><small>One owner per task</small></div><div class="board">${Object.entries(states).map(([state,label])=>{const group=tasks.filter(t=>t.state===state);return `<section class="column ${state}" aria-label="${label}"><div class="column-head"><span><span class="dot"></span>${label}</span><span class="count">${group.length}</span></div>${group.map(card).join('')}${group.length?'':`<p class="column-hint">${state==='review'?'Finished work arrives here for your review.':state==='done'?'Accepted work unlocks its dependents.':'Tasks move here as the mission progresses.'}</p>`}</section>`;}).join('')}</div>`;}
function handoff(){const review=tasks.find(t=>t.state==='review');return review?`<div class="handoff"><span class="handoff-icon">⑂</span><div><strong>Your review unlocks the next step</strong><p>${esc(review.file)} is ready. Review the result and its checks.</p></div><button class="button small" data-task="${review.id}">Review handoff ↗</button></div>`:`<div class="handoff"><span class="handoff-icon">✓</span><div><strong>${tasks.every(t=>t.state==='done')?'The mission is accepted':'Independent work can run in parallel'}</strong><p>${tasks.every(t=>t.state==='done')?'View the sample deliverables. Publishing stays a separate decision.':'Dependent tasks wait for accepted inputs, not an agent’s completion message.'}</p></div></div>`;}
function activity(){return `<aside class="activity"><div class="section-heading"><h2>Team channel</h2><small>REPLAY</small></div><p class="feed-subtitle">Handoffs and decisions, linked to tasks.</p><div class="feed" id="feed" aria-label="Simulated agent messages">${events.map(e=>`<article class="event"><div class="event-top">${e.who==='human'?'<span class="user-avatar">SP</span>':avatar(e.who,true)}<strong>${e.who==='human'?'You':agent(e.who).name}</strong><span class="muted">→ ${esc(e.to)}</span><time>${e.time}</time></div><div class="event-body">${esc(e.text)}</div><span class="event-tag">${esc(e.tag)}</span></article>`).join('')}</div><form class="message-form" id="message-form"><label for="message">Leave a note for the team</label><div class="message-input"><input id="message" maxlength="500" placeholder="e.g. Keep this local-first" required autocomplete="off"><button aria-label="Add demo note">↑</button></div><small>Saved in this demo session only. No agent will execute it.</small></form></aside>`;}
function renderView(){
  const el=$('#view-content');
  const focused=document.activeElement;
  const focusTask=focused?.dataset?.task;
  const messageFocused=focused?.id==='message';
  const selection=messageFocused?[focused.selectionStart,focused.selectionEnd]:null;
  const oldFeed=$('#feed');
  const followFeed=!oldFeed||oldFeed.scrollHeight-oldFeed.scrollTop-oldFeed.clientHeight<35;
  const oldScroll=oldFeed?.scrollTop||0;
  if(currentView==='room'){const draft=$('#message')?.value;el.innerHTML=`<div class="war-grid"><div>${board()}${handoff()}</div>${activity()}</div>`;if(draft)$('#message').value=draft;$('#feed').scrollTop=followFeed?$('#feed').scrollHeight:oldScroll;}
  if(currentView==='board')el.innerHTML=board()+handoff();
  if(currentView==='graph')el.innerHTML=graph();
  if(currentView==='artifacts')el.innerHTML=artifacts();
  if(currentView==='team')el.innerHTML=team();
  if(currentView==='research')el.innerHTML=research();
  if(messageFocused&&$('#message')){$('#message').focus({preventScroll:true});$('#message').setSelectionRange(...selection);}
  else if(focusTask&&focused.closest('#view-content'))$(`[data-task="${focusTask}"]`,el)?.focus({preventScroll:true});
}
function setView(view){
  currentView=view;
  document.querySelectorAll('[data-view]').forEach(b=>{const selected=b.dataset.view===view;b.classList.toggle('active',selected);if(b.getAttribute('role')==='tab')b.setAttribute('aria-selected',String(selected));});
  $('#crumb').textContent=({room:'War room',board:'Task board',graph:'Dependencies',artifacts:'Deliverables',team:'Agents & connections',research:'Design & research'})[view];renderView();
}
function graph(){
  const positions={'LM-01':[10,125],'LM-02':[230,20],'LM-03':[230,125],'LM-04':[230,230],'LM-05':[450,70],'LM-06':[450,205],'LM-07':[670,125]};
  const nodes=tasks.filter(t=>positions[t.id]);
  const edges=nodes.flatMap(t=>t.deps.filter(d=>positions[d]).map(d=>{const [x,y]=positions[d],[tx,ty]=positions[t.id];return `<path class="edge ${taskById(d).state==='done'?'ready':''}" marker-end="url(#arrow)" d="M${x+180},${y+38} C${x+205},${y+38} ${tx-25},${ty+38} ${tx},${ty+38}"/>`;})).join('');
  return `<div class="section-heading"><h2>Who is waiting for whom?</h2><small>Accepted outputs release dependencies</small></div><div class="graph-wrap"><svg class="graph-svg" viewBox="0 0 865 320" role="img" aria-label="Task dependency map"><defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0 0L10 5L0 10Z" fill="currentColor"/></marker></defs>${edges}${nodes.map(t=>{const [x,y]=positions[t.id];return `<g class="node" role="button" tabindex="0" data-task="${t.id}" aria-label="Open ${t.id}, ${esc(t.title)}" transform="translate(${x},${y})"><rect width="180" height="77" rx="7"/><text x="12" y="20" class="node-id">${t.id} · ${agent(t.owner).name}</text><text x="12" y="42" font-size="11">${esc(t.title.length>26?t.title.slice(0,24)+'…':t.title)}</text><text x="12" y="63" class="node-id">${states[t.state]}</text></g>`;}).join('')}</svg><p class="graph-caption">Each task consumes accepted, versioned outputs. A waiting agent uses no tokens in this concept. Completing a model response does not mark the task as accepted. Click a node to see its inputs and checks.</p></div><h3>Dependency list</h3><div class="list-panel">${tasks.map(t=>`<div class="artifact-row"><div><strong>${t.id} · ${esc(t.title)}</strong><p>${t.deps.length?'Requires '+t.deps.join(', '):'No dependencies'} · ${states[t.state]}</p></div><button class="button small" data-task="${t.id}">Inspect</button></div>`).join('')}</div>`;
}
function artifacts(){return `<div class="section-heading"><h2>Deliverables & evidence</h2><button class="button small" id="export">Export demo run</button></div><div class="note-banner">Sample files illustrate the handoff contract. They are not outputs from actual provider calls. Only submitted or accepted tasks expose their result.</div><div class="list-panel">${tasks.map(t=>`<div class="artifact-row"><span class="file-icon">▤</span><div><strong>${esc(t.file)}</strong><p>${t.id} · ${agent(t.owner).name} · ${states[t.state]}</p></div><button class="button small" data-task="${t.id}">${['review','done'].includes(t.state)?'View evidence':'View task'}</button></div>`).join('')}</div>`;}
function team(){return `<div class="section-heading"><h2>Choose the team for this project</h2><small>Role assignments are editable, not capability rankings</small></div><div class="note-banner">Configuration preview only. No accounts are connected and no keys are collected. Model APIs, native coding agents, and local inference need different adapters. A chat subscription is not automatically API credit.</div><div class="settings-grid">${agents.map(a=>`<article class="settings-card"><header>${avatar(a.id)}<div><h3>${a.name}</h3><small class="muted">${a.role}</small></div></header><p>${esc(a.scope)}</p><label>Execution adapter<select data-runtime="${a.id}" aria-label="${a.name} execution adapter">${['Model API','Coding-agent adapter','Ollama'].map(r=>`<option ${a.runtime===r?'selected':''}>${r}</option>`).join('')}</select></label><p class="small-note">${esc(a.model)}. Adapter compatibility must be checked during real setup.</p><span class="demo-label">NOT CONNECTED · DEMO CONFIGURATION</span></article>`).join('')}</div>`;}
function research(){return `<div class="research-grid"><section><div class="eyebrow">RESEARCH / 15 NOV 2026</div><h2>A control room for work that can be checked.</h2><p>The opportunity is not another collection of model tabs. It is a shared project with explicit ownership, visible dependencies, reviewable outputs and a run you can stop or resume.</p><article><h3><a href="https://github.com/kdlbs/kandev" target="_blank" rel="noopener">Kandev</a> · the closest functional reference</h3><p>Mixed agents, workflow steps, isolated workspaces and review gates. Its architecture separates the control plane from agent runtimes. Study the design; this prototype does not copy its implementation.</p></article><article><h3><a href="https://github.com/BloopAI/vibe-kanban" target="_blank" rel="noopener">Vibe Kanban</a> · board to workspace</h3><p>Connect an issue to an agent workspace, review changes and steer the session. Its README now announces a sunset; use it as a reference rather than assuming ongoing vendor support.</p></article><article><h3><a href="https://github.com/mattabate/life-distributable" target="_blank" rel="noopener">life</a> · attention and team visibility</h3><p>Role-based Claude Code sessions, an attention board, and owner decisions. REA statically inspected three selected web source files; direct source reading covered the board endpoint. This is not a full runtime audit.</p></article><article><h3>Hosted references</h3><p><a href="https://docs-platform.crewai.com/platform/en/introduction" target="_blank" rel="noopener">CrewAI AMP</a>: deployment, traces and approval controls. <a href="https://www.kimi.com/en/help/agent/agent-swarm" target="_blank" rel="noopener">Kimi Swarm</a>: parallel task progress. <a href="https://creator.poe.com/docs/server-bots/server-bots-functional-guides" target="_blank" rel="noopener">Poe</a>: multi-bot conversation context. Product features do not establish an equivalent public API.</p></article></section><section><h2>The proposed LocalMind core</h2><div class="architecture">Project brief + acceptance criteria\n                ↓\nPlanner proposes a task dependency graph\n                ↓\nPolicy + budget + scheduler\n                ↓\nBounded workers / isolated workspaces\n  Model APIs · coding agents · Ollama\n                ↓\nVersioned artifact + checks + review\n                ↓\nHuman acceptance → unblock next task\n\nDurable run events → board + team channel\nScoped context → workers, never all chats</div><h3>Three agents before six</h3><p>Implement planner → builder → reviewer first. Add other providers after this loop survives failed tools, duplicate events, cancellation and restart. Benchmark each role rather than assuming a particular brand is best.</p><h3>Keep the stack focused</h3><p>Keep React and Tauri. Add an application-owned run coordinator to the Python service, with durable task state and isolated execution. SQLite remains viable for one local coordinator; PostgreSQL belongs in a hosted team service when that is actually built.</p><h3>What the channel shows</h3><p>Explicit agent messages, tool outcomes and handoff references. It must not invent hidden reasoning or present scripted chatter as real activity.</p><h3>Why bounded?</h3><p>More agents can increase cost and coordination failures. <a href="https://www.anthropic.com/research/multi-agent-research-system" target="_blank" rel="noopener">Anthropic’s research-system report</a> describes both parallelism gains and substantially higher token use. Measure accepted output per dollar against a single-agent baseline.</p></section></div>`;}
function showDialog(html){returnFocus=document.activeElement;$('#dialog-content').innerHTML=html;$('#detail').showModal();}
function dialogHead(label,title){return `<div class="dialog-head"><div><span class="eyebrow">${esc(label)}</span><h2>${esc(title)}</h2></div><button class="icon-btn" data-close aria-label="Close dialog">×</button></div>`;}
function inspectTask(id){
  const t=taskById(id);if(!t)return;
  const ready=['review','done'].includes(t.state),blocked=missing(t);
  showDialog(`${dialogHead(`${t.id} / ${states[t.state]}`,t.title)}<div class="dialog-main"><div class="detail-meta"><span>Owner <strong>${agent(t.owner).name}</strong></span><span>Workspace <strong>task/${t.id.toLowerCase()}</strong></span><span>Revision <strong>${t.revision||1}</strong></span></div><p>${esc(t.note)}. ${blocked.length?'Blocked by '+blocked.join(', ')+'.':'All prerequisite outputs are accepted.'}</p><h3>Acceptance criteria</h3><ul class="evidence">${t.checks.map(c=>`<li><span>${ready?'✓':'○'}</span>${esc(c)}</li>`).join('')}</ul><p class="small-note demo-label">All check results in this prototype are illustrative fixtures.</p>${ready?`<h3>${esc(t.file)} · sample output</h3><pre>${esc(t.output)}</pre>`:'<p>The output becomes available when this task is submitted for review.</p>'}${t.state==='queued'?`<label class="form-field">Assign task<select class="field" id="owner">${agents.map(a=>`<option value="${a.id}" ${a.id===t.owner?'selected':''}>${a.name} · ${a.role}</option>`).join('')}</select></label><button class="button" data-reassign="${t.id}">Save assignment</button>`:''}${t.state==='review'?`<label class="check-row"><input id="reviewed" type="checkbox">I reviewed the sample output and acceptance criteria.</label><div class="dialog-actions"><button class="button primary" data-accept="${t.id}" disabled>Accept & unblock</button><button class="button" data-rework="${t.id}">Request changes</button></div>`:''}<p class="small-note">This demo does not run agents, tests, Git commands, or deployments.</p></div>`);
}
function inspectAgent(id){const a=agent(id);showDialog(`${dialogHead('AGENT PROFILE / DEMO',a.name+' · '+a.role)}<div class="dialog-main"><p>${esc(a.scope)}</p><div class="detail-meta"><span>Adapter <strong>${esc(a.runtime)}</strong></span><span>Connection <strong>Not connected</strong></span></div><h3>Assigned work</h3>${tasks.filter(t=>t.owner===id).map(t=>`<p>${t.id} · ${esc(t.title)} · ${states[t.state]}</p>`).join('')}<h3>Skills and tools are separate from the model</h3><p>In the real product, each role needs explicit tools, project context, permissions and a budget. The provider name alone does not grant browser access, code execution, image generation or a swarm.</p><button class="button" id="configure">Configure team</button></div>`);}
function newTask(){showDialog(`${dialogHead('ADD TO THIS DEMO','Give an agent a scoped task')}<form id="task-form" class="dialog-main"><label class="form-field">Task title<input class="field" id="task-title" maxlength="90" required placeholder="e.g. Review the dashboard empty states"></label><label class="form-field">Owner<select class="field" id="task-owner">${agents.map(a=>`<option value="${a.id}">${a.name} · ${a.role}</option>`).join('')}</select></label><label class="form-field">Starts after<select class="field" id="task-dep"><option value="">No dependency</option>${tasks.map(t=>`<option value="${t.id}">${t.id} · ${esc(t.title)}</option>`).join('')}</select></label><label class="form-field">Acceptance criterion<textarea class="field" id="task-check" maxlength="300" required rows="3" placeholder="What would make this task complete?"></textarea></label><p class="small-note">This adds a simulated task. The title and criterion are displayed, not sent to an AI.</p><button class="button primary">Add to board</button></form>`);}
function closeDialog(){$('#detail').close();}
$('#detail').addEventListener('close',()=>{if(returnFocus?.isConnected)returnFocus.focus();else $('#play').focus();});
document.addEventListener('click',e=>{
  const b=e.target.closest('button,a[data-view],g[data-task]');if(!b)return;
  if(b.dataset.view)setView(b.dataset.view);
  if(b.dataset.task)inspectTask(b.dataset.task);
  if(b.dataset.agent)inspectAgent(b.dataset.agent);
  if(b.hasAttribute('data-close'))closeDialog();
  if(b.dataset.accept){const t=taskById(b.dataset.accept);if(t.state!=='review'||!$('#reviewed')?.checked)return;t.state='done';t.note='Accepted with sample evidence';emit('human','Team',`${t.id} accepted. Its output is now available to dependent tasks.`,`${t.id} · accepted`);closeDialog();render();toast('Handoff accepted. Run the demo to continue unlocked work.');}
  if(b.dataset.rework){const t=taskById(b.dataset.rework);if(t.state!=='review')return;t.state='queued';t.progress=0;t.revision=(t.revision||1)+1;t.note='Changes requested · awaiting another attempt';emit('human',agent(t.owner).name,`${t.id} needs another pass against the acceptance criteria. Downstream work stays blocked.`,`${t.id} · changes requested`);closeDialog();render();}
  if(b.dataset.reassign){const t=taskById(b.dataset.reassign);if(t.state!=='queued')return;t.owner=$('#owner').value;emit('human',agent(t.owner).name,`${t.id} is now assigned to you. Existing dependencies still apply.`,`${t.id} · reassigned`);closeDialog();render();toast('Demo assignment saved.');}
  if(b.id==='configure'){closeDialog();setView('team');}
  if(b.id==='export'){const payload={simulation:true,project:'Atlas launch',tasks,events,simulatedSpend:spend};const url=URL.createObjectURL(new Blob([JSON.stringify(payload,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download='localmind-demo-run.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
});
document.addEventListener('keydown',e=>{if(e.target.matches('g[data-task]')&&['Enter',' '].includes(e.key)){e.preventDefault();inspectTask(e.target.dataset.task);}});
document.addEventListener('change',e=>{
  if(e.target.id==='reviewed')$('[data-accept]').disabled=!e.target.checked;
  if(e.target.dataset.runtime){agent(e.target.dataset.runtime).runtime=e.target.value;toast('Demo adapter selection saved. No provider is connected.');}
});
document.addEventListener('submit',e=>{
  if(e.target.id==='message-form'){e.preventDefault();const text=$('#message').value.trim();if(!text)return;emit('human','Team',text,'Human note · demo only');$('#message').value='';renderView();$('#message')?.focus();}
  if(e.target.id==='task-form'){e.preventDefault();const title=$('#task-title').value.trim(),check=$('#task-check').value.trim();if(!title||!check)return;const id=`LM-${String(tasks.length+1).padStart(2,'0')}`,owner=$('#task-owner').value,dep=$('#task-dep').value;tasks.push({id,title,owner,state:'queued',progress:0,deps:dep?[dep]:[],kind:'Custom',note:'Awaiting scheduling',file:id.toLowerCase()+'-result.md',checks:[check],output:'SIMULATED RESULT\n\nTask: '+title+'\nAcceptance: '+check+'\n\nThis fixture is not an AI-generated or verified result.'});emit('human',agent(owner).name,`${id} assigned: ${title}`,`${id} · queued`);closeDialog();render();toast('Task added to the demo board.');}
});
$('#play').addEventListener('click',()=>{if(running){stop();render();}else start();});
$('#reset').addEventListener('click',()=>{init();toast('Demo reset. No real project state was changed.');});
$('#new-task').addEventListener('click',newTask);
$('#theme').addEventListener('click',()=>{const light=document.body.classList.toggle('light');$('#theme').innerHTML=`<span>◐</span>Switch to ${light?'dark':'light'}`;});
$('#fullscreen').addEventListener('click',async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else await document.documentElement.requestFullscreen();}catch{toast('Full screen is unavailable here. You can open this file in your browser.');}});
document.addEventListener('visibilitychange',()=>{if(document.hidden&&running){stop();render();}});
init();
