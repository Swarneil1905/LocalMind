'use strict';

// Offline interaction study: every run, response, artifact and charge is a fixture.
const $ = (s) => document.querySelector(s);
const $$ = (s) => [...document.querySelectorAll(s)];
const escapeText = (v) => String(v).replace(/[&<>"']/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icons = {
  network:'M8 5h8M12 8v5M5 16v-3h14v3M9 3h6v5H9zM2 16h6v5H2zM16 16h6v5h-6z',
  chat:'M21 11a8 8 0 0 1-8 8H7l-5 3 2-6a8 8 0 0 1-1-4 9 9 0 0 1 18-1ZM7 10h10M7 14h6',
  board:'M3 4h18v16H3zM9 4v16M15 4v16M5 8h2M11 8h2M17 8h2M5 12h2M11 12h2',
  files:'M7 3h9l4 4v14H7zM16 3v5h4M3 7v14M10 12h7M10 16h5',
  book:'M3 3h7l2 2 2-2h7v16h-7l-2 2-2-2H3zM12 5v16',
  play:'m8 4 12 8-12 8z',
  pause:'M8 4v16M16 4v16',
  shield:'m12 2 9 4v6c0 5-9 10-9 10S3 17 3 12V6zM8 12l3 3 5-6',
  reset:'M3 9a9 9 0 1 1 1 9M3 3v6h6',
  fit:'M3 9V3h6M15 3h6v6M21 15v6h-6M9 21H3v-6',
  activity:'M2 12h4l3-8 6 16 3-8h4',
  check:'m5 12 4 4L19 6',
  users:'M8 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM1 21v-2a7 7 0 0 1 14 0v2M17 4a4 4 0 0 1 0 8M18 15c4 0 5 3 5 6',
  clip:'m8 13 7-7a3 3 0 0 1 4 4l-9 9a5 5 0 0 1-7-7l9-9M6 15l8-8',
  arrow:'M12 20V4m-6 6 6-6 6 6'
};
const icon = (name) => `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="${icons[name] || icons.activity}"/></svg>`;
function fillIcons(root=document) {root.querySelectorAll('[data-icon]').forEach(el=>{el.innerHTML=icon(el.dataset.icon);});}
const providers = [
  {id:'gpt',name:'GPT',role:'Project lead',file:'openai',color:'#b8a0ef',bg:'#d4e3dc',task:'Prepare the reviewed project handoff',artifact:'project-handoff.md',deps:['local'],tools:['Read artifacts','Compare revisions','Summarize results'],position:[383,78]},
  {id:'gemini',name:'Gemini',role:'Research',file:'gemini',color:'#84aaf3',bg:'#d0dcff',task:'Research accessible dashboard patterns',artifact:'research-notes.md',deps:[],tools:['Search references','Read project brief'],position:[78,295]},
  {id:'claude',name:'Claude',role:'Implementation',file:'claude',color:'#d7a993',bg:'#e2b294',task:'Build the dashboard shell',artifact:'dashboard-shell.diff',deps:[],tools:['Edit worktree','Run UI checks'],position:[383,295]},
  {id:'deepseek',name:'DeepSeek',role:'API & contracts',file:'deepseek',color:'#82b3ed',bg:'#c4d4fc',task:'Define the task-state API',artifact:'task-contract.json',deps:[],tools:['Read schema','Validate contracts'],position:[688,295]},
  {id:'kimi',name:'Kimi',role:'Visual design',file:'kimi',color:'#d2a4e1',bg:'#e7ddec',task:'Refine hierarchy and interaction states',artifact:'design-spec.md',deps:['gemini'],tools:['Inspect layout','Write design spec'],position:[78,510]},
  {id:'local',name:'Local model',role:'Quality review',file:'ollama',color:'#82c5b5',bg:'#d9e4c9',task:'Check the integrated dashboard',artifact:'qa-report.json',deps:['claude','deepseek','kimi'],tools:['Read changes','Run fixture checks'],position:[688,510]}
];
const agent = id => providers.find(a=>a.id===id);
const avatar = (id,tiny=false) => id==='human' ? '<span class="user-avatar">SP</span>' : `<span class="avatar${tiny?' tiny':''}" style="--agent-bg:${agent(id).bg}"><img src="assets/${agent(id).file}.svg" alt="" draggable="false"></span>`;
const nameOf = id => id==='team'?'Everyone':id==='human'?'You':agent(id)?.name || id;
let state, selected=null, view='overview', panel='chat', zoom=1, pan={x:0,y:0}, timer, toastTimer, attachment=null, lastFocus;
let positions = {};
const seedMessages = [
  {from:'human',to:'team',text:'Let’s build a clean project dashboard. Keep it accessible, and bring decisions back to me before the final handoff.',time:'10:24'},
  {from:'gpt',to:'team',text:'I’ve split the work. Research and implementation can run in parallel. QA will wait for the UI, design notes, and your accepted API contract.',time:'10:24',tag:'Plan shared · 6 assigned tasks'},
  {from:'gemini',to:'kimi',text:'I’m comparing navigation and empty states. I’ll send you the references and accessibility notes together.',time:'10:25',tag:'Research → design',detail:'Fixture tool event\nread_project_brief → complete\ncompare_patterns → in progress'},
  {from:'claude',to:'local',text:'The shell is taking shape. I’ll include keyboard navigation and focus states in the handoff.',time:'10:25',detail:'Fixture workspace: atlas/ui-shell\nSample files: Dashboard.tsx, navigation.css\nNo local files are being changed.'},
  {from:'deepseek',to:'human',text:'The API contract is ready for review. I added revision checks so two agents cannot overwrite the same task transition.',time:'10:26',tag:'Review requested · task-contract.json',review:true},
  {from:'local',to:'deepseek',text:'Waiting for the accepted contract and the completed UI. I’ll check the combined result once all three inputs are ready.',time:'10:26',tag:'Waiting on 3 dependencies'}
];
function reset() {
  clearInterval(timer); timer=null; clearTimeout(toastTimer);
  state={running:false,tick:0,cost:.42,seq:6,messages:structuredClone(seedMessages),tasks:{},runtimes:{},reviewed:{},notes:{}};
  for(const a of providers){state.tasks[a.id]={status:a.id==='deepseek'?'review':['gemini','claude'].includes(a.id)?'running':'queued',progress:a.id==='deepseek'?100:a.id==='gemini'?36:a.id==='claude'?23:0,attempt:1};positions[a.id]=[...a.position];state.runtimes[a.id]=a.id==='local'?'Local endpoint':'Model API';}
  selected=null;$('#channel-title').textContent='Team channel';$('#channel-subtitle').textContent='6 agents · you are in the loop';$('#recipient').value='team';$('#message').value='';clearAttachment();$('#latest').hidden=true;
  switchView('overview');switchPanel('chat');render();fitGraph();
}
function pendingReviews(){return providers.filter(a=>state.tasks[a.id].status==='review');}
function blockers(id){return agent(id).deps.filter(d=>state.tasks[d].status!=='done');}
function label(id){const s=state.tasks[id].status;return s==='running'&&!state.running?'Ready':({queued:'Waiting',running:'Working',review:'Review',done:'Done'})[s];}
function now(){return `10:${String(26+Math.floor(state.seq/3)).padStart(2,'0')}`;}
function emit(from,to,text,extra={}){state.seq++;state.messages.push({from,to,text,time:now(),...extra});}
function toast(text){$('#toast').textContent=text;$('#toast').classList.add('visible');clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('#toast').classList.remove('visible'),3200);}
function toggleRun(){
  if(providers.every(a=>state.tasks[a.id].status==='done')){toast('This demo is complete. Reset to replay it.');return;}
  state.running=!state.running;
  if(state.running){timer=setInterval(step,1600);}else{clearInterval(timer);timer=null;}
  render();
}
function step(){
  if(state.cost>=5){state.running=false;clearInterval(timer);render();toast('The demo budget has been reached.');return;}
  state.tick++;let changed=false;
  for(const a of providers){const t=state.tasks[a.id];
    if(t.status==='queued'&&blockers(a.id).length===0){t.status='running';t.progress=0;emit(a.id,'team',a.id==='gpt'?'All required outputs are ready. I’m preparing the final handoff for your review.':`Inputs accepted. Starting: ${a.task.toLowerCase()}.`,{tag:'Dependencies satisfied'});changed=true;}
    if(t.status!=='running')continue;
    const previous=t.progress;t.progress=Math.min(100,t.progress+14+(a.id==='gemini'?7:0));state.cost=Math.min(5,state.cost+.025);changed=true;
    if(previous<70&&t.progress>=70){emit(a.id,a.id==='claude'?'local':'team',progressMessage(a.id),{detail:`Fixture event · attempt ${t.attempt}\nProgress reflects the scripted replay, not real model output.`});}
    if(t.progress===100){
      t.status=['gpt','deepseek'].includes(a.id)?'review':'done';
      emit(a.id,t.status==='review'?'human':'team',t.status==='review'?`${a.artifact} is ready. Please review this revision before it moves forward.`:`${a.artifact} is ready for the next task. The sample checks are attached.`,{tag:t.status==='review'?'Human review requested':`Artifact ready · ${a.artifact}`,review:t.status==='review'});
    }
  }
  if(!providers.some(a=>state.tasks[a.id].status==='running')&&pendingReviews().length){state.running=false;clearInterval(timer);timer=null;}
  render();
  if(!changed)toast('Waiting for your review. Open the Review panel to continue.');
}
function progressMessage(id){return ({gemini:'The patterns are compared. I’m collecting source notes for the design handoff.',claude:'Keyboard navigation is represented in the fixture. Preparing the shell diff for QA.',deepseek:'Revising the contract using your feedback. QA remains blocked until you accept it.',kimi:'The design notes now cover spacing, empty states and reduced motion.',local:'Checking the combined fixture outputs. I’m listing unresolved items separately.',gpt:'The handoff includes each output, its owner and the sample verification results.'})[id];}
function review(id,accept){const t=state.tasks[id];if(t.status!=='review')return;
  if(accept&&!state.reviewed[id]){toast('Inspect the sample evidence and confirm this revision first.');return;}
  if(accept){t.status='done';emit('human',id,`Accepted ${agent(id).artifact}, revision ${t.attempt}.`,{tag:'Review accepted'});state.reviewed[id]=false;}
  else{const note=state.notes[id]?.trim();if(!note){toast('Add a short rework note so the agent has a clear next step.');return;}t.status='queued';t.progress=0;t.attempt++;state.reviewed[id]=false;emit('human',id,`Please revise: ${note}`,{tag:'Changes requested'});}
  render();renderReview();
  if(providers.every(a=>state.tasks[a.id].status==='done')){state.running=false;clearInterval(timer);render();toast('Project handoff accepted. Demo complete.');}
  else toast(accept?'Accepted. Continue the demo to run eligible tasks.':'Rework queued. Dependent tasks remain blocked.');
}
function render(){
  document.body.classList.toggle('playing',state.running);
  $('#run').innerHTML=icon(state.running?'pause':'play')+`<span>${state.running?'Pause':'Run demo'}</span>`;
  const done=providers.filter(a=>state.tasks[a.id].status==='done').length;
  $('#run-status').innerHTML=`<i class="status-dot ${state.running?'running':'ready'}"></i> ${done===6?'Complete':state.running?'Demo running':state.tick?'Paused':'Ready to run'}`;
  $('#complete-count').textContent=`${done} of 6`;$('#spend').textContent=`$${state.cost.toFixed(2)}`;$('#sidebar-cost').innerHTML=`$${state.cost.toFixed(2)} <em>/ $5.00</em>`;$('#budget-bar').style.width=`${state.cost/5*100}%`;
  $$('.mini-bars i').forEach((el,i)=>el.classList.toggle('complete',i<done));
  const pending=pendingReviews();$('#review-count').textContent=pending.length?`${pending.length} needs your review`:'No pending reviews';$('#review-badge').textContent=pending.length;$('#gate-status').textContent=done===6?'Handoff accepted':'Review before handoff';
  $('#message-count').textContent=state.messages.length;$('#output-count').textContent=providers.filter(a=>['done','review'].includes(state.tasks[a.id].status)).length;
  $('#decision-nudge').hidden=!pending.length;
  if(pending.length){$('#decision-nudge small').textContent=pending[0].id==='deepseek'?'Review the API contract to unblock QA.':'Review the integrated project handoff.';}
  $('#execution-title').textContent=done===6?'Your project handoff is accepted':state.running?'Work is moving through the team':pending.length&&state.tick?'Your review is the next step':'Your team is ready';
  $('#execution-text').textContent=done===6?'Every sample task is complete. Inspect the outputs or export this session.':state.running?'Solid nodes show ownership; animated connectors show eligible handoffs.':pending.length&&state.tick?'The demo is paused. Read the evidence, then approve or request changes.':'Start the replay to see agents work in parallel and pass results forward.';
  $('#agent-list').innerHTML=providers.map(a=>`<button class="agent-item ${selected===a.id?'selected':''}" data-agent="${a.id}" aria-pressed="${selected===a.id}">${avatar(a.id)}<span><strong>${a.name}</strong><small>${a.role}</small></span><span class="status-dot ${state.tasks[a.id].status}"></span></button>`).join('');
  renderNodes();renderEdges();renderMessages();renderBoard();renderOutputs();
  if(panel==='context')renderContext();
  // Keep a human's in-progress review and composer stable during run updates.
  if(panel==='review'&&$('#review-panel').dataset.key!==pending.map(a=>a.id+state.tasks[a.id].attempt).join(','))renderReview();
}
function renderNodes(){
  $('#nodes').innerHTML=`<button class="human-node" style="left:407px;top:0" data-human="true"><span class="user-avatar">SP</span>You · project owner</button><span class="graph-note" style="left:380px;top:245px">DELEGATE · BUILD IN PARALLEL</span>`+providers.map(a=>{const t=state.tasks[a.id],pos=positions[a.id];return `<div class="node ${t.status} ${selected===a.id?'selected':''}" data-node="${a.id}" style="left:${pos[0]}px;top:${pos[1]}px;--node-color:${a.color}" role="button" tabindex="0" aria-label="${a.name}, ${a.role}, ${label(a.id)}" aria-pressed="${selected===a.id}"><button class="drag-handle" data-drag="${a.id}" aria-label="Move ${a.name} node (drag with pointer)">⠿</button><div class="node-top">${avatar(a.id)}<div><span class="node-name">${a.role}</span><span class="node-provider">${a.name}${a.id==='local'?' · on device':''}</span></div><span class="node-status"><i class="status-dot ${t.status}"></i>${label(a.id)}</span></div><span class="node-task">${a.task}</span><div class="node-foot"><span>${blockers(a.id).length?`${blockers(a.id).length} prerequisites`:t.status==='review'?'Needs your decision':t.status==='done'?'Output ready':`Attempt ${t.attempt} · demo`}</span><span>${t.progress}%</span></div><div class="node-track"><i style="width:${t.progress}%"></i></div></div>`;}).join('')+`<button class="human-node decision" style="left:383px;top:525px" data-human="true">${icon('shield')}<strong>${pendingReviews().length?'Your review unlocks the next step':'You stay in control'}</strong><small>${pendingReviews().length?'Inspect output · approve or revise':'Review requested outputs here'}</small></button>`;
}
function pathBetween(from,to){const a=positions[from],b=positions[to];let x1=a[0]+117,y1=a[1]+137,x2=b[0]+117,y2=b[1];const mid=(y1+y2)/2;return `M ${x1} ${y1} C ${x1} ${mid}, ${x2} ${mid}, ${x2} ${y2}`;}
function renderEdges(){
  const relations=[['gpt','gemini','delegation'],['gpt','claude','delegation'],['gpt','deepseek','delegation'],['gemini','kimi','dependency'],['deepseek','local','dependency'],['claude','local','dependency'],['kimi','local','dependency']];
  let svg='<defs><marker id="arrowhead" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#68738e"/></marker></defs><path class="edge-base edge-complete" d="M500 38 V78" marker-end="url(#arrowhead)"/>';
  relations.forEach(([a,b,type])=>{let d=pathBetween(a,b);if(a==='kimi'&&b==='local'){const p=positions.kimi,q=positions.local;d=`M${p[0]+234} ${p[1]+112} C ${p[0]+335} ${p[1]+112}, ${q[0]-100} ${q[1]+112}, ${q[0]} ${q[1]+112}`;}
    const complete=state.tasks[a].status==='done';const live=state.tasks[b].status==='running'&&(type==='delegation'||complete);
    svg+=`<path class="edge-base ${complete?'edge-complete':''}" d="${d}" marker-end="url(#arrowhead)"/>${live?`<path class="edge-live" style="--edge-color:${agent(b).color}" d="${d}"/>`:''}<path class="edge-hit" d="${d}" data-edge="${a}:${b}:${type}" role="button" tabindex="0" aria-label="Inspect ${type} from ${agent(a).name} to ${agent(b).name}"/>`;
  });
  // Approval is separate from delegation and dependency edges.
  svg+='<path class="edge-base" d="M805 432 C805 473 500 468 500 525" stroke="#9f7a49"/><text class="edge-label" x="483" y="490">REVIEW GATE</text>';
  $('#edges').innerHTML=svg;
}
function selectAgent(id){selected=id;$('#recipient').value=id||'team';$('#channel-title').textContent=id?`${agent(id).name} · ${agent(id).role}`:'Team channel';$('#channel-subtitle').textContent=id?'Direct messages & shared handoffs':'6 agents · you are in the loop';render();if(panel==='context')renderContext();$('#feed').scrollTop=$('#feed').scrollHeight;}
function messageHTML(m){return `<article class="message ${m.from==='human'?'human':''}"><div class="message-top">${avatar(m.from,true)}<strong>${nameOf(m.from)}</strong><span class="to">→ ${escapeText(nameOf(m.to))}</span><time>${escapeText(m.time)}</time></div><div class="message-body">${escapeText(m.text)}${m.image?`<img class="message-image" src="${m.image}" alt="Locally attached screenshot">`:''}</div>${m.tag?`<button class="tag ${m.review?'review-tag':''}" data-message-action="${m.review?'review':'artifact'}" data-source="${m.from}">${m.review?icon('shield'):''}${escapeText(m.tag)} ↗</button>`:''}${m.detail?`<details><summary>Tool activity · simulated</summary><pre>${escapeText(m.detail)}</pre></details>`:''}</article>`;}
function renderMessages(){
  const feed=$('#feed'),atBottom=feed.scrollHeight-feed.scrollTop-feed.clientHeight<55,oldScroll=feed.scrollTop;
  const ms=selected?state.messages.filter(m=>m.from===selected||m.to===selected||m.to==='team'):state.messages;
  const key=String(state.seq)+':'+selected;
  if(feed.dataset.key!==key){feed.innerHTML='<div class="feed-date">TODAY · SCRIPTED SESSION</div>'+ms.map(messageHTML).join('');feed.dataset.key=key;if(atBottom){feed.scrollTop=feed.scrollHeight;$('#latest').hidden=true;}else{feed.scrollTop=oldScroll;$('#latest').hidden=false;}}
  const full=$('#full-feed');if(full.dataset.key!==String(state.seq)){const scroll=full.scrollTop;full.innerHTML=state.messages.map(messageHTML).join('');full.dataset.key=state.seq;full.scrollTop=scroll;}
}
function renderBoard(){const groups=[['queued','Waiting'],['running','Working'],['review','Needs review'],['done','Complete']];$('#task-board').innerHTML=groups.map(([s,title])=>`<section class="task-column"><h3>${title} · ${providers.filter(a=>state.tasks[a.id].status===s).length}</h3>${providers.filter(a=>state.tasks[a.id].status===s).map(a=>`<button class="task-card" data-agent="${a.id}"><small>AT-${providers.indexOf(a)+1} · ${a.role}</small><strong>${a.task}</strong><p>${blockers(a.id).length?'Waiting for '+blockers(a.id).map(nameOf).join(', '):label(a.id)}</p><footer>${avatar(a.id,true)}${a.name} <span>↗</span></footer></button>`).join('')}</section>`).join('');}
function renderOutputs(){const items=providers.filter(a=>['review','done'].includes(state.tasks[a.id].status));$('#output-list').innerHTML=items.map(a=>`<button class="output-row" data-artifact="${a.id}">${icon('files')}<span><strong>${a.artifact}</strong><small>${a.name} · revision ${state.tasks[a.id].attempt} · sample artifact</small></span><span class="output-status">${state.tasks[a.id].status==='review'?'Needs review':'Ready'} ↗</span></button>`).join('')||'<p class="empty-panel">Outputs appear as the demo progresses.</p>';}
function renderContext(){const a=selected?agent(selected):null;$('#context-panel').innerHTML=a?`<div class="detail-eyebrow">AGENT CONTEXT</div><div class="detail-card"><h3>${a.name} · ${a.role}</h3><p>${a.task}</p><div class="detail-row"><span>Adapter</span><b>${escapeText(state.runtimes[a.id])} · simulated</b></div><div class="detail-row"><span>Workspace</span><b>atlas/${a.id}</b></div><div class="detail-row"><span>Waiting for</span><b>${blockers(a.id).map(nameOf).join(', ')||'No prerequisites'}</b></div><div class="detail-row"><span>Output</span><b>${a.artifact}</b></div></div><div class="detail-eyebrow">SCOPED TOOLS · CONCEPT</div><div class="pill-list">${a.tools.map(t=>`<span>${t}</span>`).join('')}</div><div class="detail-card" style="margin-top:20px"><h3>Shared project brief</h3><p>A useful, accessible project dashboard with an explicit review step. The assigned model can change without changing this role.</p><p>No files or credentials are available to this prototype.</p></div><button class="button" data-artifact="${a.id}">Inspect sample output ↗</button>`:`<div class="detail-eyebrow">SHARED PROJECT BRIEF</div><div class="detail-card"><h3>Atlas dashboard</h3><p>Build an accessible workspace with clear navigation, task state and reviewable outputs.</p><div class="detail-row"><span>Owner</span><b>You</b></div><div class="detail-row"><span>Scope</span><b>This project only</b></div><div class="detail-row"><span>Review gates</span><b>API contract + final handoff</b></div><div class="detail-row"><span>Execution</span><b>Scripted local demo</b></div></div><div class="detail-eyebrow">HOW THE TEAM WORKS</div><div class="detail-card"><p>Research and implementation begin in parallel. Design uses the research. QA waits for all inputs, including your accepted API contract. GPT prepares the final handoff after QA completes.</p><p>Select an agent to inspect its individual context and tools.</p></div>`;}
function evidence(id){return ({deepseek:'SAMPLE CONTRACT · NOT EXECUTED\nPATCH /tasks/:id\n{ expected_revision, next_status, artifact_id }\n\nAcceptance criteria:\n✓ stale revisions rejected\n✓ project scope checked\n✓ only accepted inputs unblock dependents\n\nThese checks are fixture labels, not actual test results.',gpt:'SAMPLE HANDOFF · NOT EXECUTED\nIncluded: research, UI diff, contract, design notes, QA report.\n\nReview the integrated result and unresolved issues.\nNo branch has been merged or published.',gemini:'SAMPLE RESEARCH NOTES\nNavigation: keep the active project visible.\nEmpty states: explain the next useful action.\nAccessibility: retain focus and support reduced motion.',claude:'SAMPLE DIFF · NOT APPLIED\n+ <nav aria-label="Project navigation">\n+ <button aria-current="page">Overview</button>\n+ </nav>\n\nFixture checks: focus order, navigation, empty state.',kimi:'SAMPLE DESIGN NOTES\nOne active task per owner.\nStable labels alongside colored status.\nAnimations reflect work; pause and reduced motion remain available.',local:'SAMPLE QA REPORT · NOT EXECUTED\n{ "inputs": ["shell", "contract", "design"],\n  "fixture_checks": ["keyboard", "scope", "review gate"],\n  "actual_tests_run": false }'})[id];}
function renderReview(){const pending=pendingReviews();$('#review-panel').dataset.key=pending.map(a=>a.id+state.tasks[a.id].attempt).join(',');$('#review-panel').innerHTML=pending.length?`<div class="detail-eyebrow">HUMAN DECISION</div>`+pending.map(a=>`<section class="detail-card review-card"><h3>${a.id==='gpt'?'Accept the project handoff?':'Accept the API contract?'}</h3><p>${a.id==='gpt'?'This completes the sample project. Nothing is published.':'QA can start after this contract and the remaining inputs are ready.'}</p><span class="review-artifact">${a.artifact} · revision ${state.tasks[a.id].attempt}</span><pre class="review-evidence">${escapeText(evidence(a.id))}</pre><label class="review-check"><input type="checkbox" data-review-check="${a.id}" ${state.reviewed[a.id]?'checked':''}> I inspected this sample revision.</label><label class="detail-eyebrow" for="note-${a.id}">REWORK NOTE</label><textarea class="review-note" id="note-${a.id}" data-note="${a.id}" placeholder="What should change?">${escapeText(state.notes[a.id]||'')}</textarea><div class="review-actions"><button class="button primary" data-approve="${a.id}" ${state.reviewed[a.id]?'':'disabled'}>Accept & unblock</button><button class="button" data-rework="${a.id}">Request changes</button></div></section>`).join(''):'<div class="empty-panel">✓ No pending decisions.<br>Continue the demo to move the project forward. Completed reviews are recorded in the conversation.</div>';}
function switchPanel(next){panel=next;$$('[data-panel]').forEach(b=>{b.classList.toggle('active',b.dataset.panel===next);b.setAttribute('aria-selected',String(b.dataset.panel===next));});['chat','context','review'].forEach(p=>$('#'+p+'-panel').hidden=p!==next);if(next==='context')renderContext();if(next==='review')renderReview();}
function switchView(next){view=next;$$('[data-view]').forEach(b=>{b.classList.toggle('active',b.dataset.view===next);if(b.hasAttribute('role'))b.setAttribute('aria-selected',String(b.dataset.view===next));});['overview','chat','tasks','outputs'].forEach(v=>$('#'+v+'-view').hidden=v!==next);if(next==='overview')requestAnimationFrame(fitGraph);}
function openReview(){switchPanel('review');if(innerWidth<850)$('#review-panel').scrollIntoView({block:'center',behavior:'smooth'});}
function modal(title,html){lastFocus=document.activeElement;$('#dialog-title').textContent=title;$('#dialog-body').innerHTML=html;$('#dialog').showModal();}
function artifact(id){if(!agent(id))return;modal(agent(id).artifact,`<p>Sample artifact · ${agent(id).name} · revision ${state.tasks[id].attempt}</p><pre>${escapeText(evidence(id))}</pre><p class="small-print">This output illustrates the review experience. No real code, files, model calls or tests produced it.</p>`);}
function applyTransform(){const stage=$('#graph-stage');stage.style.transform=`translate(${pan.x}px,${pan.y}px) scale(${zoom})`;$('#zoom-value').textContent=`${Math.round(zoom*100)}%`;}
function fitGraph(){const viewport=$('#graph-viewport');if(!viewport.clientWidth)return;zoom=Math.min(viewport.clientWidth/1030,viewport.clientHeight/690,1.1);pan={x:(viewport.clientWidth-1000*zoom)/2,y:(viewport.clientHeight-670*zoom)/2};applyTransform();}
function zoomBy(delta){const v=$('#graph-viewport'),old=zoom;zoom=Math.min(1.5,Math.max(.28,zoom+delta));pan.x=v.clientWidth/2-(v.clientWidth/2-pan.x)*zoom/old;pan.y=v.clientHeight/2-(v.clientHeight/2-pan.y)*zoom/old;applyTransform();}
let drag=null,moved=false;
$('#graph-viewport').addEventListener('pointerdown',e=>{if(e.button!==0)return;const h=e.target.closest('[data-drag]');if(!h&&e.target.closest('.node,.human-node,.edge-hit'))return;drag={id:h?.dataset.drag,x:e.clientX,y:e.clientY,pan:{...pan},pos:h?[...positions[h.dataset.drag]]:null};moved=false;$('#graph-viewport').setPointerCapture(e.pointerId);});
$('#graph-viewport').addEventListener('pointermove',e=>{if(!drag)return;const dx=e.clientX-drag.x,dy=e.clientY-drag.y;if(Math.abs(dx)+Math.abs(dy)>4)moved=true;if(drag.id){positions[drag.id]=[Math.max(0,Math.min(766,drag.pos[0]+dx/zoom)),Math.max(65,Math.min(530,drag.pos[1]+dy/zoom))];const n=$(`[data-node="${drag.id}"]`);n.style.left=positions[drag.id][0]+'px';n.style.top=positions[drag.id][1]+'px';renderEdges();}else{pan.x=drag.pan.x+dx;pan.y=drag.pan.y+dy;applyTransform();}});
$('#graph-viewport').addEventListener('pointerup',()=>{drag=null;setTimeout(()=>moved=false,0);});
$('#graph-viewport').addEventListener('pointercancel',()=>drag=null);
function clearAttachment(){if(attachment?.url)URL.revokeObjectURL(attachment.url);attachment=null;$('#attachment-preview').innerHTML='';$('#file').value='';}
function addAttachment(file){if(!file)return;if(!['image/png','image/jpeg','image/webp'].includes(file.type)||file.size>5*1024*1024){toast('Choose a PNG, JPEG or WebP image under 5 MB.');return;}clearAttachment();attachment={file,url:URL.createObjectURL(file)};$('#attachment-preview').innerHTML=`<div class="attachment-chip"><img src="${attachment.url}" alt="Screenshot preview"><span>${escapeText(file.name||'Pasted screenshot')}</span><button type="button" id="remove-image" aria-label="Remove attachment">×</button></div>`;}
$('#composer').addEventListener('submit',async e=>{e.preventDefault();const text=$('#message').value.trim(),to=$('#recipient').value;if(!text&&!attachment)return;let image=null;if(attachment){const blob=attachment.file;image=await new Promise(resolve=>{const r=new FileReader();r.onload=()=>resolve(r.result);r.onerror=()=>resolve(null);r.readAsDataURL(blob);});}emit('human',to,text||'Screenshot attached.',{image});$('#message').value='';clearAttachment();selected=to==='team'?null:to;selectAgent(selected);switchPanel('chat');emit(to==='team'?'gpt':to,'human','Demo reply: your message is recorded in this session. A connected agent would use it as guidance for its next step.',{tag:'Simulated acknowledgment'});render();$('#feed').scrollTop=$('#feed').scrollHeight;$('#message').focus();});
$('#message').addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing){e.preventDefault();$('#composer').requestSubmit();}});
$('#message').addEventListener('paste',e=>{const image=[...(e.clipboardData?.items||[])].find(i=>i.type.startsWith('image/'));if(image){e.preventDefault();addAttachment(image.getAsFile());}});
$('#composer').addEventListener('dragover',e=>e.preventDefault());$('#composer').addEventListener('drop',e=>{e.preventDefault();addAttachment(e.dataTransfer.files[0]);});$('#file').addEventListener('change',e=>addAttachment(e.target.files[0]));
document.addEventListener('click',e=>{
  const b=e.target.closest('button,[data-node],.edge-hit');if(!b||moved)return;
  if(b.dataset.view){switchView(b.dataset.view);return;}
  if(b.dataset.panel){switchPanel(b.dataset.panel);return;}
  if(b.dataset.agent||b.dataset.node){selectAgent(b.dataset.agent||b.dataset.node);if(view==='tasks')switchPanel('context');return;}
  if(b.dataset.human){openReview();return;}
  if(b.dataset.approve){review(b.dataset.approve,true);return;}
  if(b.dataset.rework){review(b.dataset.rework,false);return;}
  if(b.dataset.artifact){artifact(b.dataset.artifact);return;}
  if(b.dataset.edge){const [a,c,t]=b.dataset.edge.split(':');modal(`${nameOf(a)} → ${nameOf(c)}`,`<p><b>${t==='delegation'?'Assignment':'Task dependency'}</b></p><p>${t==='delegation'?`${nameOf(a)} assigns a bounded task to ${nameOf(c)}. This connection does not imply a completed result.`:`${nameOf(c)} waits for the accepted output from ${nameOf(a)}.`}</p><h3>Related conversation</h3>${state.messages.filter(m=>(m.from===a&&m.to===c)||(m.from===c&&m.to===a)).map(m=>`<p><b>${nameOf(m.from)}:</b> ${escapeText(m.text)}</p>`).join('')||'<p>No direct messages yet. This is a planned workflow connection.</p>'}`);return;}
  if(b.dataset.messageAction){if(b.dataset.messageAction==='review')openReview();else if(agent(b.dataset.source))artifact(b.dataset.source);else toast('This decision is recorded in the session.');return;}
});
document.addEventListener('keydown',e=>{if((e.key==='Enter'||e.key===' ')&&e.target.matches('[data-node],.edge-hit')){e.preventDefault();e.target.dispatchEvent(new MouseEvent('click',{bubbles:true}));}if((e.ctrlKey||e.metaKey)&&['1','2','3','4'].includes(e.key)){e.preventDefault();switchView(['overview','chat','tasks','outputs'][Number(e.key)-1]);}});
document.addEventListener('change',e=>{if(e.target.dataset.reviewCheck){const id=e.target.dataset.reviewCheck;state.reviewed[id]=e.target.checked;$(`[data-approve="${id}"]`).disabled=!e.target.checked;}if(e.target.dataset.runtime)state.runtimes[e.target.dataset.runtime]=e.target.value;});
document.addEventListener('input',e=>{if(e.target.dataset.note)state.notes[e.target.dataset.note]=e.target.value;});
$('#run').onclick=toggleRun;$('#next-step').onclick=()=>{if(!state.running)step();else toast('Pause the demo to advance one step at a time.');};$('#reset').onclick=()=>{reset();toast('Demo reset. No project files were changed.');};$('#all-agents').onclick=()=>selectAgent(null);$('#open-review').onclick=openReview;$('#review-nudge').onclick=openReview;$('#fit').onclick=fitGraph;$('#zoom-in').onclick=()=>zoomBy(.12);$('#zoom-out').onclick=()=>zoomBy(-.12);$('#attach').onclick=()=>$('#file').click();$('#mention').onclick=()=>$('#recipient').focus();$('#message-team').onclick=()=>{selectAgent(null);switchPanel('chat');$('#message').focus();$('#composer').scrollIntoView({block:'center'});};$('#latest').onclick=()=>{$('#feed').scrollTop=$('#feed').scrollHeight;$('#latest').hidden=true;};
$('#attachment-preview').onclick=e=>{if(e.target.closest('#remove-image'))clearAttachment();};
$('#close-dialog').onclick=()=>$('#dialog').close();$('#dialog').addEventListener('close',()=>lastFocus?.isConnected&&lastFocus.focus());
$('#help').onclick=()=>modal('Welcome to Agent Hub',`<p>Coordinate a team, follow its handoffs, and decide what moves forward. This is an offline UI prototype with a deterministic demo.</p><h3>Try it</h3><p>Run the demo. Select an agent or a connector. Open Review, inspect the API fixture and accept it. Continue until the final handoff arrives. You can request changes, send a direct message, or paste an image into the composer.</p><p>Drag the canvas to pan; use the zoom controls for detail. Hover a node to reveal its drag handle. Keyboard users can select nodes and connectors with Tab and Enter. Reduced-motion preferences disable the animations.</p><p class="small-print">All costs, messages, checks and results are fixtures. No API calls, shell commands, model connections or uploads occur. Reloading clears the session.</p>`);
$('#project-info').onclick=()=>modal('Atlas workspace',`<p>A sample project for exploring multi-agent collaboration. The team is building an accessible dashboard.</p><div class="detail-row"><span>Owner</span><b>Swarneil Pradhan</b></div><div class="detail-row"><span>Environment</span><b>Offline prototype</b></div><div class="detail-row"><span>Cloud calls</span><b>None</b></div><p>Roles and models are separate. The names shown are provider families, not verified model versions or a ranking of their abilities.</p>`);
$('#configure').onclick=()=>modal('Configure your team',`<p>Try different adapter types. These selections affect the displayed configuration only; no accounts are connected.</p>${providers.map(a=>`<div class="config-row"><span>${avatar(a.id,true)}${a.name}</span><select aria-label="${a.name} adapter" data-runtime="${a.id}">${['Model API','Coding-agent runtime','Local endpoint'].map(v=>`<option ${state.runtimes[a.id]===v?'selected':''}>${v}</option>`).join('')}</select></div>`).join('')}<p class="small-print">A model API does not automatically include the tools or swarm features of its provider’s app. Real connections need supported credentials and a tested adapter.</p>`);
$('#research').onclick=()=>modal('Design & research',`<p>Agent Hub combines the flow view from your references with a shared conversation and explicit human decisions. Motion marks active work and stops when paused.</p><h3>Component references</h3><p><a href="https://21st.dev/community/components/explore/chat-components" target="_blank" rel="noopener noreferrer">21st.dev chat components</a> · <a href="https://preview.21st.dev/%40klyv-ui/components/klyv-animated-beam" target="_blank" rel="noopener noreferrer">Animated connectors</a> · <a href="https://preview.21st.dev/%40iamtoribryan/components/chat-composer" target="_blank" rel="noopener noreferrer">Chat composer</a></p><h3>Popular repositories examined</h3><table class="source-table"><tr><th>Repository</th><th>Stars observed</th></tr>${[['langgenius/dify','161,296'],['microsoft/autogen','61,876'],['crewAIInc/crewAI','60,996'],['FlowiseAI/Flowise','55,574'],['langchain-ai/langgraph','45,039'],['camel-ai/camel','18,040']].map(([r,n])=>`<tr><td><a href="https://github.com/${r}" target="_blank" rel="noopener noreferrer">${r}</a></td><td>${n}</td></tr>`).join('')}</table><p class="small-print">Snapshot: November 16, 2026. Selected relevant repositories, not an exhaustive ranking. Stars do not measure reliability.</p><p>REA was used on selected AutoGen Studio frontend files. The original prototype implements its own state machine, SVG graph and message views; it does not embed AutoGen or copy a component library.</p><p><a href="RESEARCH.md" target="_blank" rel="noopener noreferrer">Read the source notes and limitations ↗</a></p>`);
$('#export').onclick=()=>{const exportState={prototype:'LocalMind Agent Hub',simulated:true,tasks:state.tasks,runtimes:state.runtimes,cost:state.cost,messages:state.messages.map(({image,...m})=>({...m,attachment:image?'Image omitted from export':undefined}))};const url=URL.createObjectURL(new Blob([JSON.stringify(exportState,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='localmind-agent-hub-demo.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
window.addEventListener('resize',()=>{if(view==='overview')fitGraph();});window.addEventListener('pagehide',()=>{clearInterval(timer);clearAttachment();});
fillIcons();$('#avatar-stack').innerHTML=providers.map(a=>avatar(a.id,true)).join('');reset();
