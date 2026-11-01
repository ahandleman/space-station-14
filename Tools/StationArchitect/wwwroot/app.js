import {clone,measurementKey,locate,ownerList,roomEntries,roomPaths,uniqueId,renameItem,removeItem,reparentRoom,duplicateRoom,setField,programStats,maintenanceId} from './authoring.js';
import {sourceFields,choicesHtml,roomSystemsHtml,roomPlacementHtml,roomDocksHtml,connectionFeaturesHtml} from './program-panels.js';

const $=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let program, templates, report, selected='$', tab='edit', query='', undo=[], redo=[], revision=0, timer;
let yaml='', yamlDirty=false, yamlRevision=-1, pressureState=null, pressureResult=null;
const api=async(path,body,text=false)=>{
    const response=await fetch('/api/'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':text?'text/plain':'application/json'},body:text?body:JSON.stringify(body)});
    const result=response.headers.get('content-type')?.includes('json')?await response.json():await response.text();
    if(!response.ok) throw new Error(typeof result==='string'?result:result.detail||result.error||'Request failed.');
    return result;
};
function toast(message,error=false){$('toast').textContent=message;$('toast').className=error?'error':'';$('toast').hidden=false;clearTimeout(toast.timer);toast.timer=setTimeout(()=>$('toast').hidden=true,5500);}
function status(message){$('status').textContent=message;}
function record(){undo.push({program:clone(program),selected});if(undo.length>40)undo.shift();redo=[];}
function changed(message='Draft changed; checking requirements…'){
    revision++;yamlRevision=-1;pressureState=null;pressureResult=null;report=null;
    try{localStorage.setItem('station-architect-v2',JSON.stringify(program));}catch{}
    status(message);renderTree();renderHeader();renderHistory();renderReview();clearTimeout(timer);timer=setTimeout(check,220);
}
function history(from,to){if(!from.length)return;to.push({program:clone(program),selected});const saved=from.pop();program=saved.program;selected=saved.selected;changed('Restored draft from history.');renderEditor();}
function renderHistory(){$('undo').disabled=!undo.length;$('redo').disabled=!redo.length;}
function selectedNode(){return locate(program,selected)||locate(program,'$');}
function choose(path){selected=path;renderTree();renderHeader();renderEditor();renderReview();}
function renderHeader(){const node=selectedNode();$('title').textContent=node.kind==='station'?program.name||'Station program':node.kind==='maintenance'?'True maintenance':node.item.name||node.item.id;
    $('breadcrumb').textContent=node.kind==='station'?'STATION / REQUIREMENTS':`${node.ownerId||node.item.id} / ${node.kind==='room'?'ROOM REQUIREMENTS':'DEPARTMENT REQUIREMENTS'}`;
    $('item-actions').innerHTML=['department','maintenance'].includes(node.kind)?'<button data-action="add-room">+ Room</button>'+(node.kind==='department'?'<button data-action="remove" class="danger">Remove department</button>':''):node.kind==='room'?'<button data-action="add-child-room">+ Internal room</button><button data-action="duplicate-room">Duplicate</button><button data-action="remove" class="danger">Remove room</button>':'';
}
function renderTree(){if(!program)return;
    const match=item=>!query||`${item.id} ${item.name} ${item.tags?.join(' ')}`.toLowerCase().includes(query);
    const subtreeMatch=room=>match(room)||(room.rooms||[]).some(subtreeMatch);
    let html=`<button class="tree-row owner ${selected==='$'?'selected':''}" data-path="$"><span class="dot" style="background:#9bdbc5"></span><span class="tree-name">${esc(program.name||program.id)}</span></button>`;
    for(const owner of ownerList(program)){
        const rooms=owner.rooms.filter(room=>match(owner)||subtreeMatch(room));if(query&&!match(owner)&&!rooms.length)continue;
        const color=/^#[a-f0-9]{6}$/i.test(owner.color)?owner.color:'#798064';
        html+=`<button class="tree-row owner ${selected===owner.id?'selected':''}" data-path="${esc(owner.id)}"><span class="dot" style="background:${color}"></span><span class="tree-name">${esc(owner.name||owner.id)}</span><span class="meta">${owner.rooms.length}</span></button>`;
        function roomRow(room,parentPath,depth){const path=parentPath+'/'+room.id;
            html+=`<button class="tree-row room ${esc(room.role)} ${selected===path?'selected':''}" data-path="${esc(path)}" style="padding-left:${26+depth*15}px"><span class="tree-name">${depth?'↳ ':''}${esc(room.name||room.id)}</span><span class="meta" title="Instance count bounds per parent">${room.placement?.policy==='stationwide'?'W ':room.placement?.policy==='distributed'?'D ':''}${room.role==='filler'?'F ':''}${room.count.min}–${room.count.max}</span></button>`;
            for(const child of room.rooms||[])if(match(owner)||subtreeMatch(child))roomRow(child,path,depth+1);}
        for(const room of rooms)roomRow(room,owner.id,0);
    }$('tree').innerHTML=html;
}
function field(label,path,value,type='text',extra='') {return `<label>${esc(label)}<input data-field="${path}" type="${type}" value="${esc(value)}" ${type==='number'?'required':''} ${extra}></label>`;}
function checkField(label,path,value){return `<label class="check"><input data-field="${path}" type="checkbox" ${value?'checked':''}>${esc(label)}</label>`;}
function selectField(label,path,value,values){return `<label>${esc(label)}<select data-field="${path}">${values.map(option=>`<option value="${option}" ${value===option?'selected':''}>${esc(option)}</option>`).join('')}</select></label>`;}
function rangeCard(item,path,title,note){const range=item[path];return `<div class="card"><h2>${title}</h2><p>${note}</p><div class="fields four">${field('Minimum',path+'.min',range.min,'number','min="0" step="1"')}${field('Target',path+'.target',range.target,'number','min="0" step="1"')}${field('Maximum',path+'.max',range.max,'number','min="0" step="1"')}${field('Pressure weight',path+'.weight',range.weight,'number','min="0" step="0.1"')}</div></div>`;}
function shapeCard(item){return `<div class="card"><h2>Shape limits</h2><p>Aspect ratio is the longer / shorter bounding-box side. Fill ratio measures owned floor inside that box, so a winding thin room cannot pass as a square room.</p><div class="fields three">${field('Maximum aspect ratio','shape.maxAspectRatio',item.shape.maxAspectRatio,'number','min="1" step="0.1"')}${field('Minimum fill ratio','shape.minFillRatio',item.shape.minFillRatio,'number','min="0" max="1" step="0.05"')}${field('Shape pressure weight','shape.weight',item.shape.weight,'number','min="0" step="0.1"')}</div></div>`;}
function roomTable(rooms,owner){return `<div class="card"><h2>Contained rooms</h2><p>Each room owns its usable floor. Nested child rooms add their own floor to the parent complex and department budgets without double counting.</p><table class="room-list"><thead><tr><th>Room</th><th>Role</th><th>Instances</th><th>Floor / instance</th></tr></thead><tbody>${rooms.map(room=>`<tr><td><button data-path="${esc(owner+'/'+room.id)}">${esc(room.name||room.id)}</button></td><td><span class="pill ${esc(room.role)}">${esc(room.role)}</span></td><td>${room.count.min} / ${room.count.target} / ${room.count.max}</td><td>${room.area.min} / ${room.area.target} / ${room.area.max}</td></tr>`).join('')}</tbody></table>${!rooms.length?'<p class="empty">Add rooms to give this department usable floor.</p>':''}</div>`;}
function connectionsHtml(rules,local){return `<div class="card"><h2>${local?'Room':'Station'} connections</h2><p>${local?'Every instance of this room is the source. Bare targets refer to sibling rooms.':'Use Department or Department/Room paths.'} Targets can use “all” or “any”. “requireTarget” makes a target necessary when the source exists; “ifPresent” permits absent optional endpoints.</p>${rules.map((rule,index)=>`<div class="connection" data-rule="${index}"><div class="connection-title"><span class="pill">${esc(rule.priority)} · ${esc(rule.id)}</span><button data-action="remove-connection" data-index="${index}" class="danger">Remove</button></div><div class="fields">${ruleField('ID','id',rule.id,index)}${!local?ruleField('From path','from',rule.from,index):''}${ruleField('Target paths (comma separated)','to',rule.to.join(', '),index)}${ruleSelect('Target selection','selection',rule.selection,['all','any'],index)}${ruleSelect('Priority','priority',rule.priority,['required','desired','avoided'],index)}${ruleSelect('Optional target policy','presence',rule.presence,['ifPresent','requireTarget'],index)}${connectionFeaturesHtml(rule,index)}${ruleField('Weight','weight',rule.weight,index,'number','min="0" step="0.1"')}${ruleField('Maximum distance (blank = none)','maxDistance',rule.maxDistance??'',index,'number','min="1" step="1"')}<label>Allowed routes<select data-rule-field="modes" data-index="${index}" multiple>${['direct','department','publicHall','maintenance','controlledMaintenance','utilityNetwork','disposalShortcut','mail'].map(mode=>`<option value="${mode}" ${rule.modes.includes(mode)?'selected':''}>${mode}</option>`).join('')}</select><small>Ctrl / Cmd to select several. These are alternatives. controlledMaintenance requires maintenance at both ends and real controlled station transitions between them. utilityNetwork checks actual cable or pipe continuity; it does not grant crew access.</small></label><label class="wide">Description<textarea data-rule-field="description" data-index="${index}">${esc(rule.description)}</textarea></label></div></div>`).join('')}<button data-action="add-connection">+ Connection</button></div>`;}
function ruleField(label,key,value,index,type='text',extra=''){return `<label>${label}<input data-rule-field="${key}" data-index="${index}" type="${type}" value="${esc(value)}" ${extra}></label>`;}
function ruleSelect(label,key,value,values,index){return `<label>${label}<select data-rule-field="${key}" data-index="${index}">${values.map(option=>`<option value="${option}" ${value===option?'selected':''}>${option}</option>`).join('')}</select></label>`;}
function renderEditor(){if(!program)return;document.querySelectorAll('[data-tab]').forEach(button=>button.setAttribute('aria-selected',String(button.dataset.tab===tab)));
    if(tab==='yaml'){renderYaml();return;}if(tab==='pressure'){renderPressure();return;}
    const {item,kind,ownerId}=selectedNode();let html='';
    if(kind==='station'){
        html=`<div class="card"><h2>Station definition</h2><div class="fields">${field('Stable ID','id',item.id)}${field('Name','name',item.name)}<label class="wide">Description<textarea data-field="description">${esc(item.description)}</textarea></label>${sourceFields(item)}</div></div><div class="card"><h2>Hierarchical pressures</h2><p>A room transfer is scored at the room and its ancestors. An internal transfer keeps the department total constant; a transfer between departments changes both department totals. Hard limits remain independent of these weights.</p><div class="fields three">${field('Room pressure','settings.roomPressureWeight',item.settings.roomPressureWeight,'number','min="0" step="0.1"')}${field('Department pressure','settings.departmentPressureWeight',item.settings.departmentPressureWeight,'number','min="0" step="0.1"')}${field('Connection pressure','settings.connectionPressureWeight',item.settings.connectionPressureWeight,'number','min="0" step="0.1"')}</div></div>${choicesHtml(item)}${connectionsHtml(item.connections,false)}`;
    }else{
        if(kind!=='maintenance')html+=`<div class="card"><h2>${kind==='room'?'Room declaration':'Department'}</h2><div class="fields">${field('Stable ID','id',item.id)}${field('Name','name',item.name)}<label class="wide">Description<textarea data-field="description">${esc(item.description)}</textarea></label>${field('Grouping tags (comma separated)','tags',item.tags.join(', '))}${kind==='department'?field('Display color','color',item.color,'color'):selectField('Room role','role',item.role,['activity','filler'])}${sourceFields(item)}</div></div>`;
        if(kind==='room')html+=rangeCard(item,'count','Instances','Minimum above zero makes a room mandatory. Optional rooms use zero minimum. Repeatable filler rooms can use a larger maximum.');
        html+=rangeCard(item,'area',kind==='room'?'Usable floor per instance':'Total owned room floor',kind==='room'?'Minimum and maximum are final hard limits. The target and pressure weight guide growth. Walls do not count.':'The total is the sum of floor owned by all room instances, including filler. Public halls and maintenance passages are excluded.');
        if(kind!=='maintenance')html+=shapeCard(item);
        if(kind==='department')html+=`<div class="card"><h2>Department splits</h2><p>Room clusters can straddle hallways when allowed. Disconnected components and crossings incur separate costs. These high default costs favor a single coherent department.</p><div class="fields">${checkField('Allow splitting','split.allow',item.split.allow)}${field('Maximum components','split.maxComponents',item.split.maxComponents,'number','min="1" step="1"')}${field('Extra component penalty','split.componentPenalty',item.split.componentPenalty,'number','min="0" step="10"')}${field('Hall crossing penalty','split.hallCrossingPenalty',item.split.hallCrossingPenalty,'number','min="0" step="10"')}${field('Maintenance crossing penalty','split.maintenanceCrossingPenalty',item.split.maintenanceCrossingPenalty,'number','min="0" step="10"')}</div></div>${roomTable(item.rooms,item.id)}`;
        if(kind==='maintenance')html+=`<div class="card"><h2>Maintenance passages</h2><p>Passages connect department service entrances and true maintenance rooms. Passage floor is separate from the room budget above. Larger spaces must become rooms rather than widening these passages.</p><div class="fields">${field('Minimum clear width','passageWidth.min',item.passageWidth.min,'number','min="2" max="3" step="1"')}${field('Maximum clear width','passageWidth.max',item.passageWidth.max,'number','min="2" max="3" step="1"')}</div></div>${roomTable(item.rooms,maintenanceId)}`;
        if(kind==='room')html+=`<div class="card"><h2>Movement, access & space</h2><p>Access labels are authored data. A filler room can carry movement, while a sensitive room can forbid through traffic. Space frontage means sealed exterior walls or windows.</p><div class="fields">${field('Access label','access.level',item.access.level)}${field('Minimum space frontage (tiles)','exterior.minSpaceFrontage',item.exterior.minSpaceFrontage,'number','min="0" step="1"')}${checkField('Allow through traffic','allowThroughTraffic',item.allowThroughTraffic)}${checkField('Allow public entry','access.allowPublicEntry',item.access.allowPublicEntry)}${checkField('Allow maintenance entry','access.allowMaintenanceEntry',item.access.allowMaintenanceEntry)}${checkField('Allow exterior frontage','exterior.allowSpaceExposure',item.exterior.allowSpaceExposure)}</div></div>${roomPlacementHtml(item,selected.split('/').length>2)}${roomTable(item.rooms||[],selected)}${roomSystemsHtml(item)}${roomDocksHtml(item)}${connectionsHtml(item.connections,true)}`;
        if(kind==='room'){
            const parents=[...ownerList(program).map(owner=>({path:owner.id,name:owner.name||owner.id})),...roomEntries(program).filter(entry=>entry.path!==selected&&!entry.path.startsWith(selected+'/')).map(entry=>({path:entry.path,name:entry.path}))];
            html+=`<div class="card"><h2>Move this room in the hierarchy</h2><label>Parent<select id="room-parent-target">${parents.map(parent=>`<option value="${esc(parent.path)}" ${selectedNode().parentPath===parent.path?'selected':''}>${esc(parent.name)}</option>`).join('')}</select></label><p></p><button data-action="move-room-parent">Move room and its children</button><p class="note">Dependencies follow the moved subtree. In an existing design, changed definition paths retain the old geometry for explicit reconciliation.</p></div>`;
            if(item.complexArea)html+=rangeCard(item,'complexArea','Whole-complex floor budget','This optional parent budget counts this room and all descendants once, excluding walls.')+'<button data-action="remove-complex-area">Remove optional complex budget</button>';
            else if(item.rooms?.length)html+='<div class="card"><button data-action="enable-complex-area">+ Whole-complex floor budget</button></div>';
        }
    }$('editor').innerHTML=html;
}
async function check(){if(!program)return;const at=revision;try{const result=await api('compile',program);if(at!==revision)return;report=result;renderReview();status(result.success?'Requirements compiled. Geometric feasibility is checked in later stages.':'Draft retained. Review the requirement issues.');}catch(error){if(at!==revision)return;report={success:false,diagnostics:[{path:'$',code:'check-request',message:error.message,severity:'error'}]};renderReview();toast(error.message,true);status('Unable to check this draft.');}}
function treeNode(path){if(path==='$')path='$station';const walk=node=>node&&(node.path===path?node:(node.children||[]).map(walk).find(Boolean));return walk(report?.tree);}
function renderReview(){if(!program)return;const stats=programStats(program);$('summary').innerHTML=`<div class="summary">${[['Departments',stats.departments],['Room types',stats.rooms],['Required instances',stats.required],['Filler types',stats.filler]].map(([label,value])=>`<div class="stat"><strong>${value}</strong><span>${label}</span></div>`).join('')}</div>`;
    $('badge').className='badge'+(report&&!report.success?' invalid':'');$('badge').textContent=report?(report.success?'COMPILED':'NEEDS WORK'):'CHECKING';
    const node=treeNode(selected),budget=node?.effectiveFloorBudget||node?.floorBudget;const current=selectedNode();
    $('budget').innerHTML=budget?`<div class="budget"><h3>${esc(current.kind==='station'?'Station room budget':current.item.name||current.item.id||'True maintenance')}</h3><p>Feasible room-floor interval<br><b>${budget.min.toLocaleString()}–${budget.max.toLocaleString()} tiles</b></p><p>Preferred room total <b>${budget.target.toLocaleString()}</b></p><p>Bounds combine room counts, room areas and parent limits. Connections may further restrict a choice; these checks do not prove geometric packability.</p></div>`:'';
    $('diagnostics').innerHTML=report?(report.diagnostics.length?report.diagnostics.map(d=>`<div class="diagnostic ${esc(d.severity)}"><strong>${esc(d.path)} · ${esc(d.code)}</strong>${esc(d.message)}</div>`).join(''):'<div class="diagnostic info"><strong>Program checks passed</strong>Counts, floor budgets, references and policies are consistent. No geometry has been generated.</div>'):'<p class="note">Checking the program…</p>';
}
async function renderYaml(){const at=revision;$('editor').innerHTML='<p class="note">Preparing YAML…</p>';try{if(yamlRevision!==revision&&!yamlDirty){yaml=await api('yaml/export',program);if(at!==revision)return;yamlRevision=revision;}
    if(tab!=='yaml')return;$('editor').innerHTML=`<div class="yaml-bar"><button data-action="apply-yaml" class="primary">Apply YAML</button><button data-action="refresh-yaml">Use form values</button><span>Schema 2 · parsed and validated locally</span></div><p class="note">Apply source to update the tree and forms. An invalid source stays here for correction. Save draft preserves it.</p><textarea id="yaml-source" spellcheck="false" aria-label="Station program YAML">${esc(yaml)}</textarea>`;
    }catch(error){toast(error.message,true);}}
async function applyYaml(source){const result=await api('yaml/import',source,true);if(!result.success){report=result;renderReview();toast('YAML needs corrections; the source is retained.',true);return false;}record();program=result.document;selected='$';report=result;yaml=source;yamlDirty=false;try{localStorage.removeItem('station-architect-v2-source');}catch{}changed('Loaded room-first station program.');yamlRevision=revision;render();return true;}
async function download(content,name,type){const url=URL.createObjectURL(new Blob([content],{type})),anchor=document.createElement('a');anchor.href=url;anchor.download=name;anchor.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function render(){renderTree();renderHeader();renderHistory();renderEditor();renderReview();}
function readValue(target){if(target.type==='checkbox')return target.checked;if(target.type==='number')return target.value===''?null:Number(target.value);return target.value;}
function rules(){return selectedNode().item.connections;}
function addRoom(nested=false){const node=selectedNode(),container=node.kind==='room'?(nested?node.item:node.container):node.item,parentPath=node.kind==='room'?(nested?node.path:node.parentPath):(node.ownerId||node.item.id);record();container.rooms??=[];const room=clone(templates.room);room.id=uniqueId(container.rooms,'Room');room.name=room.id;if(nested){room.access.allowPublicEntry=false;room.access.allowMaintenanceEntry=false;}container.rooms.push(room);selected=parentPath+'/'+room.id;changed();render();}
function handleClick(event){const button=event.target.closest('button');if(!button)return;const data=button.dataset;
    if(data.path){choose(data.path);return;}if(data.tab){tab=data.tab;renderEditor();return;}if(!data.action)return;
    try{const node=selectedNode();switch(data.action){
        case'add-room':addRoom();return;
        case'add-child-room':addRoom(true);return;
        case'duplicate-room':record();selected=duplicateRoom(program,selected);break;
        case'move-room-parent':record();selected=reparentRoom(program,selected,$('room-parent-target').value);break;
        case'enable-complex-area':record();node.item.complexArea={min:node.item.area.min,target:node.item.area.target,max:node.item.area.max+(node.item.rooms||[]).reduce((sum,room)=>sum+room.area.max*room.count.max,0),weight:1};break;
        case'remove-complex-area':record();node.item.complexArea=null;break;
        case'remove':record();removeItem(program,selected);selected=node.kind==='room'?node.parentPath:'$';toast('Removed declaration. Surviving dependencies remain visible for review.');break;
        case'add-connection':{record();const rule=clone(templates.connection);rule.id=uniqueId(rules(),'Connection');rules().push(rule);break;}
        case'remove-connection':record();rules().splice(Number(data.index),1);break;
        case'add-content':{record();const content=clone(templates.content);content.id=uniqueId(node.item.contents,'Content');node.item.contents.push(content);break;}
        case'remove-content':record();node.item.contents.splice(Number(data.index),1);break;
        case'add-dock':{record();const docks=node.item.exterior.docks??=[],dock=clone(templates.dock);dock.id=uniqueId(docks,'Dock');dock.contentId=node.item.contents.find(content=>content.placement==='spaceBoundary'&&content.interfaceType==='dockingPort')?.id||'';docks.push(dock);break;}
        case'remove-dock':record();node.item.exterior.docks.splice(Number(data.index),1);break;
        case'add-choice':{record();const choice=clone(templates.choice);choice.id=uniqueId(program.choices,'Choice');program.choices.push(choice);break;}
        case'remove-choice':record();program.choices.splice(Number(data.index),1);break;
        case'apply-yaml':applyYaml($('yaml-source').value).catch(error=>toast(error.message,true));return;
        case'refresh-yaml':yamlDirty=false;yamlRevision=-1;try{localStorage.removeItem('station-architect-v2-source');}catch{}renderYaml();return;
        case'build-pressure':buildPressure().catch(error=>toast(error.message,true));return;
        case'compare-transfer':compareTransfer().catch(error=>toast(error.message,true));return;
        default:return;
    }changed();render();}catch(error){toast(error.message,true);}}
document.addEventListener('click',handleClick);
const listValue = value => value.split(/[,\n]/).map(v=>v.trim()).filter(Boolean);
document.addEventListener('change',event=>{
    const target=event.target,data=target.dataset;
    if(!data.field&&!data.ruleField&&!data.contentField&&!data.choiceField&&!data.dockField)return;
    if(!target.checkValidity()){toast('Enter a value within the field limits.',true);renderEditor();return;}
    try{
        record();
        if(data.field){
            const key=data.field,value=readValue(target);
            if(key==='id')selected=renameItem(program,selected,value);
            else setField(selectedNode().item,key,['tags','sourceRefs','access.entryOwners','access.requireNetworkApproach'].includes(key)?listValue(String(value)):value);
        }else if(data.ruleField){
            const key=data.ruleField,rule=rules()[Number(data.index)];
            rule[key]=key==='to'?listValue(target.value):['modes','interfaceTypes'].includes(key)?Array.from(target.selectedOptions,option=>option.value):readValue(target);
        }else if(data.contentField){
            const key=data.contentField,content=selectedNode().item.contents[Number(data.index)];
            if(key==='id')for(const dock of selectedNode().item.exterior?.docks||[])if(dock.contentId===content.id)dock.contentId=String(readValue(target));
            setField(content,key,key==='prototypes'?listValue(target.value):readValue(target));
        }else if(data.dockField){
            const key=data.dockField,dock=selectedNode().item.exterior.docks[Number(data.index)];
            setField(dock,key,key==='directions'?Array.from(target.selectedOptions,option=>option.value):key==='sourceRefs'?listValue(target.value):readValue(target));
        }else{
            const key=data.choiceField,choice=program.choices[Number(data.index)];
            setField(choice,key,key==='rooms'?listValue(target.value):readValue(target));
        }
        changed();renderHeader();renderReview();
    }catch(error){undo.pop();toast(error.message,true);renderEditor();}
});
document.addEventListener('input',event=>{if(event.target.id==='yaml-source'){yaml=event.target.value;yamlDirty=true;try{localStorage.setItem('station-architect-v2-source',yaml);}catch{}}});
$('search').addEventListener('input',event=>{query=event.target.value.toLowerCase();renderTree();});
$('undo').addEventListener('click',()=>history(undo,redo));$('redo').addEventListener('click',()=>history(redo,undo));$('validate').addEventListener('click',()=>tab==='yaml'&&yamlDirty?applyYaml(yaml).catch(error=>toast(error.message,true)):check());
$('add-department').addEventListener('click',()=>{record();const department=clone(templates.department);department.id=uniqueId(program.departments,'Department');department.name=department.id;program.departments.push(department);selected=department.id;changed();render();});
$('reset').addEventListener('click',async()=>{try{const fresh=await api('default');record();program=fresh;selected='$';yamlDirty=false;yaml='';try{localStorage.removeItem('station-architect-v2-source');}catch{}changed('Loaded default program. Undo restores the previous draft.');render();}catch(error){toast(error.message,true);}});
$('open').addEventListener('click',()=>$('file').click());$('file').addEventListener('change',async event=>{const file=event.target.files[0];if(!file)return;try{if(file.size>2*1024*1024)throw new Error('Program files must fit within 2 MiB.');const source=await file.text();if(file.name.toLowerCase().endsWith('.json')){const draft=JSON.parse(source);const loaded=await api('draft/import',draft.document||draft);record();program=loaded.document;selected='$';report=loaded.report;yamlDirty=false;yaml='';try{localStorage.removeItem('station-architect-v2-source');}catch{}changed('Opened draft.');if(typeof draft.yamlSource==='string'){yaml=draft.yamlSource;yamlDirty=true;tab='yaml';try{localStorage.setItem('station-architect-v2-source',yaml);}catch{}}render();}else{yaml=source;yamlDirty=true;tab='yaml';renderEditor();await applyYaml(source);}}catch(error){toast(error.message,true);}finally{event.target.value='';}});
$('save-json').addEventListener('click',()=>download(JSON.stringify({document:program,...(yamlDirty?{yamlSource:yaml}:{})},null,2),(program.id||'station')+'.draft.json','application/json'));
$('save-tree').addEventListener('click',async()=>{try{const compiled=await api('compile',program);download(JSON.stringify(compiled,null,2),(program.id||'station')+'.requirements.json','application/json');status('Exported the compiled tree, connections, remaining choices and diagnostics.');}catch(error){toast(error.message,true);}});
$('use-in-designer').addEventListener('click',()=>{try{localStorage.setItem('station-architect-program-transfer',JSON.stringify(program));location.href='designer.html';}catch(error){toast('Unable to transfer this draft: '+error.message,true);}});
$('save-yaml').addEventListener('click',async()=>{try{const source=yamlDirty?yaml:await api('yaml/export',program);download(source,(program.id||'station')+'.yaml','application/yaml');status(yamlDirty?'Exported the current YAML source, including any unfinished edits.':'Exported room-first YAML definition.');}catch(error){toast(error.message,true);}});
document.addEventListener('keydown',event=>{if(event.target.matches('input,textarea,select'))return;if((event.ctrlKey||event.metaKey)&&event.key==='z'){event.preventDefault();history(event.shiftKey?redo:undo,event.shiftKey?undo:redo);}});

// The pressure sandbox is scalar input, never generated geometry. The server derives parent totals.
function renderPressure(){
    $('editor').innerHTML=`<div class="card"><h2>Room and ancestor pressures</h2><p>Compare a hypothetical floor transfer independently of the physical designer. Room target pressure acts locally; the sum of owned room floor applies pressure at the department. A transfer within the same department leaves its area unchanged.</p><p>This sandbox evaluates supplied measurements. Shape, frontage and connectivity are measured from real masks in the station designer. Connection requirements remain in the compiled tree.</p><button data-action="build-pressure" class="primary">${pressureState?'Reset':'Create'} target-size sample</button></div><div id="pressure-workspace"></div>`;
    if(pressureState)renderPressureWorkspace();
}
function renderPressureWorkspace(){
    const options=pressureState.rooms.map(room=>`<option value="${esc(measurementKey(room))}">${esc(measurementKey(room))} · ${room.area} tiles</option>`).join('');
    $('pressure-workspace').innerHTML=`<div class="card"><h2>Compare one transfer</h2><p>Moves floor from one room instance to another. The sample is unchanged until you build a new sample. Lower cost favors the move; hard violations remain visible even if a weighted cost falls.</p><div class="fields"> <label>Take floor from<select id="transfer-from">${options}</select></label><label>Give floor to<select id="transfer-to">${options}</select></label><label>Tiles to transfer<input id="transfer-area" type="number" min="1" value="5" step="1"></label><label>Evaluation phase<select id="pressure-phase"><option value="growth">growth</option><option value="final">final</option></select></label></div><p></p><button data-action="compare-transfer">Compare transfer ↗</button></div><div id="pressure-result"></div>`;
    if(pressureState.rooms.length>1)$('transfer-to').selectedIndex=1;
    if(pressureResult)renderPressureResult();
}
async function buildPressure(){pressureState=await api('pressure/sample',program);pressureResult=null;renderPressure();}
async function compareTransfer(){if(!pressureState)return;const from=$('transfer-from').value,to=$('transfer-to').value,area=Number($('transfer-area').value);if(from===to)throw new Error('Choose two different room instances.');if(!Number.isInteger(area)||area<=0)throw new Error('Transfer a positive whole number of tiles.');pressureResult=await api('pressure/transfer',{document:program,state:pressureState,from,to,area,phase:$('pressure-phase').value});renderPressureResult();}
function renderPressureResult(){const result=pressureResult;$('pressure-result').innerHTML=`<div class="card"><h2>Transfer evaluation</h2><div class="pressure-score">${esc(result.delta>0?'+':'')}${esc(Number(result.delta).toFixed(4))}</div><p>Change in weighted cost. ${result.acceptable?'No evaluated hard limits were violated.':'The proposed state has evaluated hard-limit violations.'} ${!result.after.complete?'Geometry and route checks remain incomplete.':''}</p><p>Before ${esc(Number(result.before.cost).toFixed(4))} → after ${esc(Number(result.after.cost).toFixed(4))}</p><div class="pressure-table">${esc(JSON.stringify(result.after,null,2))}</div></div>`;}
async function start(){try{[program,templates]=await Promise.all([api('default'),api('templates')]);
    try{const saved=localStorage.getItem('station-architect-v2');if(saved){const loaded=await api('draft/import',JSON.parse(saved));program=loaded.document;report=loaded.report;status('Restored your local draft.');}const source=localStorage.getItem('station-architect-v2-source');if(source){yaml=source;yamlDirty=true;}}catch{}
    render();await check();
    }catch(error){status('Could not load the program.');toast(error.message,true);}}
start();
