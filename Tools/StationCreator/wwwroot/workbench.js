import { createTopologyEditor } from './topology-editor.js';
import { createSpatialEditor } from './spatial-editor.js';
import { createHierarchyEditor } from './hierarchy-editor.js';

const $ = id => document.getElementById(id);
const escape = value => String(value ?? '').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const clone = value => JSON.parse(JSON.stringify(value));
const stageNames = ['Topology','Placement','Department growth','Maintenance','Validate & export'];
const stageKeys = ['topology','placement','growth','maintenance','validation'];
const stageActions = ['Check topology ↗','Grow departments →','Build maintenance →','Validate spatial plan →','Re-run validation ↗'];
let project,stage=0,busy=false,dirty=false,view,drag=null,toastTimer;
let undoStack=[],redoStack=[];
let topologyPanel='dependencies';
const canvas=$('canvas');

const topologyEditor=createTopologyEditor({
    getProject:()=>project,record:()=>record(),changed:()=>invalidate('topology'),redraw:()=>render(),drawMap:()=>renderMap(),
    notify:(message,error)=>toast(message,error),check:()=>api('/api/topology/validate',project),
    replaceTemplate:()=>applyTemplate(project.config.archetype,true),changeArchetype:archetype=>applyTemplate(archetype)
});
const spatialEditor=createSpatialEditor({
    getProject:()=>project,record:()=>record(),changed:kind=>invalidate(kind),redraw:()=>render(),drawMap:()=>renderMap(),
    notify:(message,error)=>toast(message,error),run:(key,options)=>runSpatial(key,options)
});
const hierarchyEditor=createHierarchyEditor({getProject:()=>project,record:()=>record(),changed:()=>invalidate('topology'),redraw:()=>render(),notify:toast});

function toast(message,error=false){
    $('toast').textContent=message;$('toast').className=error?'error':'';$('toast').hidden=false;
    clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').hidden=true,error?9000:4000);
}
function record(){
    undoStack.push(JSON.stringify(project));if(undoStack.length>40)undoStack.shift();redoStack=[];dirty=true;
}
function invalidate(kind){
    if(kind==='presentation'){dirty=true;project.result=null;return;}
    project.result=null;project.layout=null;
    if(kind==='topology'){
        const p=project.spatialPlan;
        if(p){
            const ids=new Set(project.config.departments.map(d=>d.id));
            const removed=p.seeds.filter(seed=>!ids.has(seed.id));
            const added=project.config.departments.filter(d=>!p.seeds.some(seed=>seed.id===d.id));
            if(p.seeds.length===ids.size&&removed.length===1&&added.length===1)removed[0].id=added[0].id;
            p.seeds=p.seeds.filter(seed=>ids.has(seed.id));
            p.regions=[];p.publicCorridors=[];p.maintenance=[];p.walls=[];p.hull=[];p.ports=[];p.structuralReserve=[];p.reservedExterior=[];p.exteriorBoundary=[];p.stage='placement';
            clearProgramGeometry(p);
        }
        project.stage='topology';topologyEditor.clearReport();
    }else if(kind!=='settings'&&project.spatialPlan){
        const p=project.spatialPlan;
        if(kind==='placement'){
            if(p.initialServiceReservations?.length)p.serviceReservations=clone(p.initialServiceReservations);
            p.regions=[];p.publicCorridors=[];p.maintenance=[];p.walls=[];p.hull=[];p.ports=[];p.structuralReserve=[];p.reservedExterior=[];p.exteriorBoundary=[];
            clearProgramGeometry(p);
        }else if(kind==='growth'){p.maintenance=[];p.hull=[];clearProgramGeometry(p);}
        else if(kind==='maintenance')p.hull=[];
        p.stage=kind;project.stage=kind;
    }
    $('status').textContent='Edited spatial draft · checks required';
}
function clearProgramGeometry(p){p.hierarchy={subdepartments:[],roomFeatures:[],internalCirculation:[]};p.deepMaintenanceRooms=[];p.commonMaintenanceRooms=[];p.solarInstallations=[];p.maintenanceSpaceAccess=[];}
function restore(direction){
    if(busy)return;
    const source=direction==='undo'?undoStack:redoStack,target=direction==='undo'?redoStack:undoStack;
    if(!source.length)return;target.push(JSON.stringify(project));project=JSON.parse(source.pop());dirty=true;
    topologyEditor.reset();spatialEditor.reset();render();$('status').textContent=direction==='undo'?'Change undone':'Change restored';
}
async function api(path,data){
    const response=await fetch(path,data===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
    if(!response.ok){const error=await response.json().catch(()=>({}));throw new Error(error.detail||error.error||`Request failed (${response.status})`);}
    return response.json();
}
function setBusy(value){
    busy=value;$('busy').hidden=!value;
    document.querySelectorAll('button,input,select,textarea').forEach(element=>element.disabled=value);
    if(!value){render();$('undo').disabled=!undoStack.length;$('redo').disabled=!redoStack.length;}
    $('status').textContent=value?'Planning station territories and routes…':'Ready';
}
function errors(){return project.result?.diagnostics?.filter(d=>d.severity==='error')||[];}
async function checkTopology(){
    if(busy)return;setBusy(true);
    try{await topologyEditor.validate();toast(topologyEditor.getReport().valid?'Topology requirements checked.':'Review the topology issues.',!topologyEditor.getReport().valid);}
    catch(error){toast(error.message,true);}finally{setBusy(false);}
}
async function applyTemplate(archetype,replaceAll=false){
    if(busy)return;record();setBusy(true);
    try{
        const candidate=clone(project);candidate.config.archetype=archetype;
        project=await api(`/api/topology/template?replaceAll=${replaceAll}`,candidate);
        topologyEditor.reset();spatialEditor.reset();stage=0;fit();
        toast(replaceAll?'Template dependencies restored. Undo restores your changes.':'Topology template changed; authored dependencies were preserved.');
    }catch(error){toast(error.message,true);}finally{setBusy(false);}
}
async function runSpatial(key,options={}){
    if(busy)return;
    if(options.record!==false)record();setBusy(true);
    try{
        project=await api(`/api/spatial/${key}`,project);
        if(!errors().length){
            if(key!=='boundaries'){stage=Math.max(1,stageKeys.indexOf(key));spatialEditor.activate(stage);}
            if(key==='placement')fit();
            toast(key==='validation'?(project.result?.success?'Spatial geometry checks passed.':'Review the spatial checks.'):key==='boundaries'?'Region boundaries and access points updated.':`${stageNames[stage]} is ready to edit.`);
        }else toast('This stage needs attention. Review the diagnostics.',true);
    }catch(error){toast(error.message,true);}finally{setBusy(false);}
}
async function generate(){
    if(busy)return;record();setBusy(true);
    try{
        project=await api('/api/spatial/generate',project);
        if(project.result?.success)stage=4;
        else{
            const failed=project.result?.diagnostics?.find(d=>d.severity==='error')?.stage;
            stage=failed==='hierarchy'?2:Math.max(1,stageKeys.indexOf(failed));
        }
        topologyEditor.reset();spatialEditor.reset();fit();
        toast(project.result?.success?'Station spatial plan generated and checked.':'The spatial plan needs attention; your topology has been preserved.',!project.result?.success);
    }catch(error){toast(error.message,true);}finally{setBusy(false);}
}
async function nextStage(){
    if(stage===0){await checkTopology();return;}
    if(!project.spatialPlan){await runSpatial('placement');return;}
    await runSpatial(stageKeys[Math.min(stage+1,4)]);
}
async function enterStage(index){
    stage=index;spatialEditor.activate(stage);
    if(index===1&&(!project.spatialPlan||project.spatialPlan.seeds.length!==project.config.departments.length||!project.spatialPlan.publicReservations.length)){await runSpatial('placement');return;}
    render();
}

function render(){
    if(!project)return;
    const p=project.spatialPlan;
    const reached=project.result?.success?4:Math.max(stageKeys.indexOf(p?.stage),stageKeys.indexOf(project.stage));
    $('stages').innerHTML=stageNames.map((name,i)=>`<button data-stage="${i}" class="${stage===i?'active':''} ${reached>=i?'done':''}"><span class="step">${String(i+1).padStart(2,'0')}</span>${name}</button>`).join('');
    $('stage-number').textContent=String(stage+1).padStart(2,'0');$('stage-title').textContent=stageNames[stage];
    $('run-stage').textContent=stage!==0&&!p?'Build placement →':stageActions[stage];
    $('stage-hint').textContent=stage===0?'Author dependencies and the room program, then place department seeds.':stage===1?'Growth uses your envelope, reservations, seeds and required room budgets.':stage===2?'Maintenance follows reserved service space, with enclosed rooms and exterior connections.':stage===3?'Checks use actual masks, room programs, ports and access policies.':'Planning geometry is checked. Equipment entities and simulation come later.';
    $('topology-next').hidden=stage!==0;
    $('topology-legend').hidden=stage!==0;$('map-legend').hidden=stage===0;
    $('graph-toggle').hidden=stage===0;$('grid-toggle').hidden=stage===0;
    document.body.classList.toggle('topology-mode',stage===0);
    document.body.classList.toggle('spatial-mode',stage!==0);
    $('view-title').textContent=stage===0?'STATION TOPOLOGY':stage===1?'ENVELOPE & PHYSICAL SEEDS':stage===2?'GROWN DEPARTMENT TERRITORIES':'STATION CIRCULATION & MAINTENANCE';
    $('canvas-help').textContent=stage===0?topologyEditor.getHelp():spatialEditor.help(stage);
    $('project-meta').textContent=`SEED ${project.config.seed} · ${project.config.archetype.toUpperCase()} · ${project.config.width} × ${project.config.height} CANVAS`;
    const tabs='<div class="program-tabs"><button data-topology-panel="dependencies" class="'+(topologyPanel==='dependencies'?'selected':'')+'">Topology & links</button><button data-topology-panel="program" class="'+(topologyPanel==='program'?'selected':'')+'">Departments & rooms</button></div>';
    $('inspector-content').innerHTML=stage===0?tabs+(topologyPanel==='program'?hierarchyEditor.inspectorHtml():topologyEditor.inspectorHtml()):spatialEditor.inspectorHtml(stage);
    renderReview();renderMap();
    $('undo').disabled=busy||!undoStack.length;$('redo').disabled=busy||!redoStack.length;
}
function diagnosticsHtml(diagnostics){
    return diagnostics.map(d=>`<div class="diagnostic ${escape(d.severity)}"><strong>${escape(d.stage)} / ${escape(d.code)}</strong>${escape(d.message)}</div>`).join('');
}
function renderReview(){
    const badge=$('result-badge');
    $('topology-details').hidden=false;
    if(stage===0){
        const report=topologyEditor.getReport();
        $('review-title').textContent=topologyPanel==='program'?'Station program':'Dependency editor';$('topology-details').innerHTML=topologyPanel==='program'?hierarchyEditor.detailsHtml():topologyEditor.detailsHtml();
        badge.className=`badge ${report?(report.valid?'success':'failure'):''}`;badge.textContent=report?(report.valid?'CONCEPT CHECKED':'NEEDS WORK'):'CONCEPT';
        $('result-summary').textContent=report?(report.valid?'Dependency rules passed. Physical routes and station systems are separate checks.':'Correct the dependency issues below; your draft stays editable.'):'Describe the relationships that shape the station. Required, Desired and Avoided apply to their selected purpose and methods.';
        $('metrics').innerHTML=topologyEditor.summary();
        $('diagnostics').innerHTML=report?(report.diagnostics.length?diagnosticsHtml(report.diagnostics):'<p class="muted">No dependency conflicts found.</p>'):'<p class="muted">Check topology to find invalid settings and conflicting requirements.</p>';
        return;
    }
    const result=project.result;
    $('review-title').textContent='Spatial plan';$('topology-details').innerHTML=spatialEditor.detailsHtml(stage);
    badge.className=`badge ${result?(result.success?'success':'failure'):''}`;badge.textContent=result?(result.success?'GEOMETRY CHECKED':'NEEDS WORK'):'SPATIAL DRAFT';
    $('result-summary').textContent=result?(result.success?'Region masks, circulation and maintenance passed the spatial checks.':'Correct the indicated mask, seed or route constraints and run this stage again.'):'Department territories, floor, walls and route reservations are separate editable layers.';
    $('metrics').innerHTML=spatialEditor.summaryHtml();
    if(result?.metrics?.hash)$('metrics').innerHTML+=`<div class="hash">SHA256 ${escape(result.metrics.hash.slice(0,24))}…</div>`;
    $('diagnostics').innerHTML=result?diagnosticsHtml(result.diagnostics):'<p class="muted">Edits clear the previous checks. Run validation after changing the plan.</p>';
}
function fit(){
    const cells=stage!==0?project?.spatialPlan?.envelope:null;
    if(cells?.length){const all=[...cells,...(project.spatialPlan.solarInstallations||[]).flatMap(s=>[...s.cells,...s.catwalk])],xs=all.map(p=>p.x),ys=all.map(p=>p.y);const minX=Math.min(...xs),minY=Math.min(...ys);view={x:minX-7,y:minY-7,w:Math.max(...xs)-minX+15,h:Math.max(...ys)-minY+15};}
    else view={x:-5,y:-5,w:(project?.config.width||144)+10,h:(project?.config.height||112)+10};
    updateView();
}
function updateView(){if(!view)return;canvas.setAttribute('viewBox',`${view.x} ${view.y} ${view.w} ${view.h}`);$('zoom-label').textContent=`${Math.round((project.config.width+10)/view.w*100)}%`;}
function zoom(factor,point=null){
    if(!view)fit();const p=point||{x:view.x+view.w/2,y:view.y+view.h/2};
    const width=Math.max(8,Math.min(800,view.w*factor)),ratio=width/view.w;
    view={x:p.x-(p.x-view.x)*ratio,y:p.y-(p.y-view.y)*ratio,w:width,h:view.h*ratio};updateView();
}
function mapPoint(event){const point=canvas.createSVGPoint();point.x=event.clientX;point.y=event.clientY;return point.matrixTransform(canvas.getScreenCTM().inverse());}
function layer(cells,color,name,opacity=1,pointerless=true){
    const rows=new Map();for(const p of cells||[]){if(!rows.has(p.y))rows.set(p.y,[]);rows.get(p.y).push(p.x);}
    let html=`<g data-layer="${escape(name)}" fill="${escape(color)}" opacity="${opacity}" ${pointerless?'pointer-events="none"':''}>`;
    for(const [y,xs]of rows){xs.sort((a,b)=>a-b);for(let i=0;i<xs.length;i++){const first=xs[i];let last=first;while(i+1<xs.length&&xs[i+1]<=last+1)last=Math.max(last,xs[++i]);html+=`<rect x="${first}" y="${y}" width="${last-first+1}" height="1"/>`;}}
    return html+'</g>';
}
function renderMap(){
    if(!project)return;if(!view)fit();const c=project.config;
    let html='<defs><pattern id="tile-grid" width="1" height="1" patternUnits="userSpaceOnUse"><path d="M1 0H0V1" fill="none" stroke="#91a3ad" stroke-opacity=".17" stroke-width=".045"/></pattern><pattern id="dock-hatch" width="2" height="2" patternUnits="userSpaceOnUse"><path d="M0 2L2 0" stroke="#6c98b3" stroke-width=".3"/></pattern></defs>';
    html+=`<rect x="0" y="0" width="${c.width}" height="${c.height}" fill="#111d27" stroke="#354b59" stroke-width=".2"/>`;
    if(stage===0)html+=topologyEditor.graphHtml();
    else{
        html+=spatialEditor.graphHtml(layer,stage);
        if($('show-graph').checked&&project.spatialPlan&&project.topology){
            const positions=new Map(project.spatialPlan.seeds.map(seed=>[seed.id,seed.position]));
            const colors={required:'#8cdbc4',desired:'#e1b96c',avoided:'#e28b96'};
            html+='<g class="physical-dependency-overlay" fill="none" pointer-events="none">';
            for(const edge of project.topology.edges){
                const a=positions.get(edge.from),b=positions.get(edge.to);if(!a||!b)continue;
                const priority=edge.priority||(edge.hard?'required':'desired');
                html+=`<path d="M${a.x},${a.y}L${b.x},${b.y}" stroke="${colors[priority]||'#9ab3bc'}" stroke-width=".35" stroke-opacity=".8" stroke-dasharray="${priority==='required'?'0':priority==='desired'?'1.5 1':'.4 1'}"><title>${escape(edge.from)} ↔ ${escape(edge.to)} · ${escape(priority)} · weight ${edge.weight}. Dependency overlay; physical walking routes are the hall and port layers.</title></path>`;
            }
            html+='</g>';
        }
    }
    if(stage!==0&&$('show-grid').checked)html+=`<rect x="0" y="0" width="${c.width}" height="${c.height}" fill="url(#tile-grid)" pointer-events="none"/>`;
    canvas.innerHTML=html;
    canvas.classList.toggle('paint',stage!==0&&spatialEditor.hasPaintTool());
    $('empty-state').hidden=stage===0?!!project.topology:!!project.spatialPlan;updateView();
}

document.addEventListener('click',event=>{
    if(busy)return;
    const panel=event.target.closest('[data-topology-panel]');if(panel){topologyPanel=panel.dataset.topologyPanel;render();return;}
    const programNode=stage===0&&topologyPanel==='program'?event.target.closest('[data-topology-node]'):null;
    if(programNode){hierarchyEditor.select(programNode.dataset.topologyNode);return;}
    if(stage===0&&topologyPanel==='program'&&hierarchyEditor.handleClick(event))return;
    if(stage===0&&topologyEditor.handleClick(event))return;
    if(stage!==0&&spatialEditor.handleClick(event))return;
    if(stage!==0&&spatialEditor.selectMap(event))return;
    const target=event.target.closest('button');if(!target)return;
    if(target.dataset.stage!==undefined){enterStage(+target.dataset.stage);return;}
    switch(target.id){
        case 'generate':case 'empty-generate':generate();break;
        case 'run-stage':nextStage();break;
        case 'topology-next':enterStage(1);break;
        case 'validate-now':stage===0?checkTopology():runSpatial('validation');break;
        case 'save-project':case 'export-json':saveProject();break;
        case 'export-svg':exportSvg();break;
        case 'open-project':$('project-file').click();break;
        case 'export-definition':exportDefinition();break;
        case 'open-definition':$('definition-file').click();break;
        case 'fit':fit();break;
        case 'zoom-in':zoom(.8);break;case 'zoom-out':zoom(1.25);break;
        case 'undo':restore('undo');break;case 'redo':restore('redo');break;
    }
});
document.addEventListener('change',event=>{
    if(busy||!project)return;
    if(stage===0&&topologyPanel==='program'&&hierarchyEditor.handleChange(event))return;
    if(stage===0&&topologyEditor.handleChange(event))return;
    if(stage!==0&&spatialEditor.handleChange(event,stage))return;
    if(event.target.id==='show-grid'||event.target.id==='show-graph')renderMap();
});
document.addEventListener('input',event=>{if(!busy&&stage===0)topologyEditor.handleInput(event);});
canvas.addEventListener('pointerdown',event=>{
    if(busy||event.button!==0)return;const point=mapPoint(event);
    if(stage===0&&topologyEditor.pointerDown(event,point))drag={kind:'topology',programNode:event.target.closest('[data-topology-node]')?.dataset.topologyNode};
    else if(stage!==0&&spatialEditor.pointerDown(event,point,stage))drag={kind:'spatial'};
    else if(stage===0&&event.target.closest('[data-topology-node],[data-topology-edge]'))return;
    else if(stage!==0&&event.target.closest('[data-spatial-region]'))return;
    else drag={kind:'pan',start:point};
    canvas.setPointerCapture(event.pointerId);
});
canvas.addEventListener('pointermove',event=>{
    if(!project||busy)return;const point=mapPoint(event);$('coordinates').textContent=`X ${Math.floor(point.x)} / Y ${Math.floor(point.y)}`;
    if(!drag)return;
    if(drag.kind==='topology')topologyEditor.pointerMove(point);
    else if(drag.kind==='spatial')spatialEditor.pointerMove(point,stage);
    else{view.x-=point.x-drag.start.x;view.y-=point.y-drag.start.y;updateView();}
});
async function endDrag(event){
    if(!drag)return;const previous=drag;drag=null;
    if(previous.kind==='topology'){topologyEditor.pointerUp(event.type==='pointercancel');if(event.type!=='pointercancel'&&topologyPanel==='program'&&previous.programNode)hierarchyEditor.select(previous.programNode);}
    else if(previous.kind==='spatial')await spatialEditor.pointerUp(stage,event.type==='pointercancel');
    render();
}
canvas.addEventListener('pointerup',endDrag);canvas.addEventListener('pointercancel',endDrag);
canvas.addEventListener('wheel',event=>{event.preventDefault();zoom(event.deltaY>0?1.12:1/1.12,mapPoint(event));},{passive:false});
document.addEventListener('keydown',event=>{
    if(event.target.matches('input,select,textarea'))return;
    if(stage===0&&topologyEditor.handleKey(event))return;
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'){event.preventDefault();restore(event.shiftKey?'redo':'undo');}
    else if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='y'){event.preventDefault();restore('redo');}
    else if(event.key.toLowerCase()==='f')fit();
    else if(event.key==='+'||event.key==='=')zoom(.8);else if(event.key==='-')zoom(1.25);
});
function download(text,name,type){const url=URL.createObjectURL(new Blob([text],{type}));const a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function saveProject(){download(JSON.stringify(project,null,2),`station-${project.config.archetype}-${project.config.seed}.json`,'application/json');dirty=false;toast('Editable station project saved.');}
async function exportDefinition(){
    try{const response=await fetch('/api/definition/export',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(project)});if(!response.ok){const error=await response.json();throw new Error(error.detail||error.error);}download(await response.text(),`station-definition-${project.config.seed}.yaml`,'application/yaml');toast('YAML definition saved: topology, program, policies and generation settings.');}catch(error){toast(error.message,true);}
}
function exportSvg(){
    const c=project.config;
    const title=stage===0?'Topology concept':project.result?.success?'Spatial geometry checked':'Spatial draft';
    const svg=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="-5 -10 ${c.width+10} ${c.height+15}"><rect x="-5" y="-10" width="${c.width+10}" height="${c.height+15}" fill="#111d27"/><text x="1" y="-4" fill="#d4e3e5" font-family="system-ui,sans-serif" font-size="2.5">SS14 STATION PLAN · ${escape(c.archetype)} · SEED ${c.seed} · ${title}</text>${canvas.innerHTML}</svg>`;
    download(svg,`${stage===0?'topology':'spatial'}-${c.archetype}-${c.seed}.svg`,'image/svg+xml');toast('Current station planning view exported.');
}
$('project-file').addEventListener('change',async event=>{
    const file=event.target.files[0];if(!file)return;
    try{
        if(file.size>32*1024*1024)throw new Error('Project file exceeds 32 MB.');
        const candidate=JSON.parse(await file.text());
        if(candidate.version!==1||!candidate.config||!Array.isArray(candidate.config.departments))throw new Error('Open a version 1 Station Creator project.');
        setBusy(true);const checked=await api('/api/import',candidate);record();project=checked;
        stage=project.spatialPlan?Math.max(1,stageKeys.indexOf(project.stage||project.spatialPlan.stage)):0;
        topologyEditor.reset();spatialEditor.reset();fit();
        toast(project.spatialPlan?'Spatial project opened; saved edits were preserved.':project.layout?'Legacy preview opened. Build physical placement to use the new region planner.':'Topology project opened.');
    }catch(error){toast(error.message,true);}finally{setBusy(false);event.target.value='';}
});
window.addEventListener('beforeunload',event=>{if(dirty){event.preventDefault();event.returnValue='';}});
$('definition-file').addEventListener('change',async event=>{
    const file=event.target.files[0];if(!file)return;
    try{
        if(file.size>2*1024*1024)throw new Error('Definition file exceeds 2 MB.');
        setBusy(true);const response=await fetch('/api/definition/import',{method:'POST',headers:{'Content-Type':'text/plain'},body:await file.text()});
        if(!response.ok){const error=await response.json();throw new Error(error.detail||error.error);}
        const imported=await response.json();record();project=imported;stage=0;topologyPanel='program';topologyEditor.reset();spatialEditor.reset();hierarchyEditor.reset();fit();toast('YAML definition opened. Build placement to generate its station.');
    }catch(error){toast(error.message,true);}finally{setBusy(false);event.target.value='';}
});
try{project=await api('/api/default');fit();render();}catch(error){toast(`Could not connect to Station Creator: ${error.message}`,true);}
