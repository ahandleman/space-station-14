const esc = value => String(value ?? '').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const field = (name,body,help='') => `<label class="field">${esc(name)}${body}${help?`<small class="field-help">${esc(help)}</small>`:''}</label>`;
const input = (name,value,key,type='text',extra='') => field(name,`<input data-program-field="${key}" type="${type}" value="${esc(value)}" ${extra}>`);
const check = (name,value,key) => `<label class="toggle policy-toggle"><input data-program-field="${key}" type="checkbox" ${value?'checked':''}> ${esc(name)}</label>`;
const text = (name,value,key) => field(name,`<textarea data-program-field="${key}" rows="3" maxlength="2000">${esc(value)}</textarea>`);
const select = (name,value,key,choices) => field(name,`<select data-program-field="${key}">${choices.map(([id,label])=>`<option value="${esc(id)}" ${id===value?'selected':''}>${esc(label)}</option>`).join('')}</select>`);
const row = (...children)=>`<div class="field-row">${children.join('')}</div>`;

// Paths are stable authoring IDs, separate from human-readable names and geometry.
export function renameProgramPath(project,oldPath,newPath) {
    const replace = path => path===oldPath?newPath:path?.startsWith(oldPath+'/')?newPath+path.slice(oldPath.length):path;
    for(const department of project.config.departments)for(const sub of department.subdepartments||[])for(const room of sub.rooms||[])room.accessibleFrom=(room.accessibleFrom||[]).map(replace);
    for(const edge of project.config.hierarchyRelations||[]){edge.from=replace(edge.from);edge.to=replace(edge.to);}
}
export function removeProgramPath(project,path) {
    const matches = value => value===path||value?.startsWith(path+'/');
    project.config.hierarchyRelations=(project.config.hierarchyRelations||[]).filter(e=>!matches(e.from)&&!matches(e.to));
    for(const d of project.config.departments)for(const s of d.subdepartments||[])for(const r of s.rooms||[])r.accessibleFrom=(r.accessibleFrom||[]).filter(p=>!matches(p));
}

export function createHierarchyEditor({getProject,record,changed,redraw,notify}) {
    let selected='',relationIndex=-1;
    const project=()=>getProject();
    const departments=()=>project().config.departments;
    function allPaths(){return departments().flatMap(d=>[d.id,...(d.subdepartments||[]).flatMap(s=>[`${d.id}/${s.id}`,...(s.rooms||[]).map(r=>`${d.id}/${s.id}/${r.id}`)])]);}
    function locate(){const [di,si,ri]=selected.split('/');const d=departments().find(x=>x.id===di),s=d?.subdepartments?.find(x=>x.id===si),r=s?.rooms?.find(x=>x.id===ri);return {d,s,r,item:r||s||d,kind:r?'room':s?'subdepartment':'department'};}
    function unique(items,prefix){let n=1;while(items.some(x=>x.id===prefix+n))n++;return prefix+n;}
    function update(){changed();redraw();}
    function options(value){return allPaths().map(path=>`<option value="${esc(path)}" ${path===value?'selected':''}>${esc(path)}</option>`).join('');}
    function inspectorHtml(){
        if(!selected||!allPaths().includes(selected))selected=departments()[0]?.id||'';
        return `<p class="description">Define the station's departments, subdepartments and room program before allocating floor. Required internal access keeps Atmos and Salvage inside their parent routes.</p><div class="section-label">STATION PROGRAM <button class="text-button" data-program-action="add-department">+ Department</button></div><div class="program-tree">${departments().map(d=>`<button class="department-row ${selected===d.id?'selected':''}" data-program-select="${esc(d.id)}"><i style="background:${/^#[0-9a-f]{6}$/i.test(d.color)?d.color:'#6e90b0'}"></i><span>${esc(d.id)}</span><small>${(d.subdepartments||[]).length} groups</small></button>${(d.subdepartments||[]).map(s=>`<button class="program-child ${selected===d.id+'/'+s.id?'selected':''}" data-program-select="${esc(d.id+'/'+s.id)}">${esc(s.name||s.id)} <small>${s.requireInternalAccess?'internal access':''}</small></button>${selected.startsWith(d.id+'/'+s.id)?(s.rooms||[]).map(r=>`<button class="program-room ${selected===d.id+'/'+s.id+'/'+r.id?'selected':''}" data-program-select="${esc(d.id+'/'+s.id+'/'+r.id)}">${esc(r.name||r.id)} <small>${r.minCount}–${r.maxCount}</small></button>`).join(''):''}`).join('')}`).join('')}</div><div class="section-label">SCOPED DEPENDENCIES <button class="text-button" data-program-action="add-relation">+ Add</button></div><p class="field-help">Use department/subdepartment/room paths. Weights prioritize Desired and Avoided rules; Required stays mandatory. The map checks supported walking and adjacency rules.</p>${(project().config.hierarchyRelations||[]).map((e,i)=>`<button class="program-relation ${relationIndex===i?'selected':''}" data-program-relation="${i}"><strong>${esc(e.from)} → ${esc(e.to)}</strong><span>${esc(e.priority)} · ${esc(e.relation)} · weight ${e.weight}</span></button>`).join('')||'<p class="muted">Use room access origins for shared rooms such as the TEG, or add a weighted dependency here.</p>'}`;
    }
    function detailsHtml(){
        const relations=project().config.hierarchyRelations||[],edge=relations[relationIndex];
        if(edge)return edgeDetails(edge);
        const {d,s,r,item,kind}=locate();if(!item)return '<p class="muted">Select a program item to edit its requirements.</p>';
        let html=`<div class="section-label">${esc(kind.toUpperCase())}</div><p class="program-path">${esc(selected)}</p>${input('Stable ID',item.id,'id','text','maxlength="48" pattern="[^/]+"')}`;
        if(kind!=='department')html+=input('Display name',item.name||item.id,'name');
        html+=text('Description / purpose',item.description||'','description');
        if(kind==='department'){
            html+=input('Placement order (lower first)',d.placementOrder??100,'placementOrder','number','min="-10000" max="10000"')+check('Use as the topology hub',d.topologyHub,'topologyHub')+'<p class="field-help">Order applies when rebuilding placement. The hub role applies when creating a topology template; existing authored links stay editable.</p>';
            html+=select('Grouping policy',d.grouping||'preferGrouped','grouping',[['grouped','Keep grouped'],['preferGrouped','Prefer grouped, allow satellites'],['independent','Allow dispersed groups']])+check('Allow department to split',d.allowSplit??true,'allowSplit');
            html+=row(input('Minimum floor',d.minArea,'minArea','number','min="1" max="8000"'),input('Maximum floor',d.maxArea,'maxArea','number','min="1" max="8000"'));
            html+=select('Access',d.access,'access',[['public','Public'],['restricted','Restricted'],['secure','Secure']])+check('Reserve exterior docking frontage',d.exterior,'exterior');
            html+='<button class="full" data-program-action="add-subdepartment">+ Add subdepartment</button>';
        }else if(kind==='subdepartment'){
            html+=row(input('Minimum floor',s.minArea,'minArea','number','min="1" max="8000"'),input('Target floor',s.targetArea,'targetArea','number','min="1" max="8000"'))+input('Maximum floor',s.maxArea,'maxArea','number','min="1" max="8000"');
            html+=check('Require access through parent department',s.requireInternalAccess,'requireInternalAccess')+check('Allow subdepartment to split',s.allowSplit,'allowSplit');
            html+='<p class="field-help">Connected groups prefer their parent. Permitted satellites can use another compatible department floor allocation. Required internal access prevents satellite placement.</p><button class="full" data-program-action="add-room">+ Add room / feature</button>';
        }else{
            html+=select('Type',r.kind,'kind',[['room','Room'],['feature','Equipment / feature reservation']])+row(input('Minimum count',r.minCount,'minCount','number','min="0" max="16"'),input('Maximum count',r.maxCount,'maxCount','number','min="0" max="16"'))+row(input('Minimum usable floor',r.minArea,'minArea','number','min="1" max="1000"'),input('Maximum usable floor',r.maxArea,'maxArea','number','min="1" max="4000"'));
            html+=field('Required access origins',`<select data-program-field="accessibleFrom" multiple size="5">${allPaths().filter(p=>p!==selected).map(p=>`<option value="${esc(p)}" ${(r.accessibleFrom||[]).includes(p)?'selected':''}>${esc(p)}</option>`).join('')}</select>`,'Select multiple origins with Ctrl or Cmd. Engineering and Engineering/Atmos both access the TEG.');
        }
        html+=`<div class="separator"></div><button class="full danger" data-program-action="remove-item">Remove ${kind} and its dependencies</button><p class="field-help">Changes apply immediately. Undo restores the item and its relationships. The planner fits minimum room counts; maxima cap later additions. Room and feature masks reserve space; equipment entities are a later stage.</p>`;
        return html;
    }
    function edgeDetails(edge){
        const choose=(title,key,values)=>field(title,`<select data-program-edge="${key}">${values.map(([value,label])=>`<option value="${esc(value)}" ${edge[key]===value?'selected':''}>${esc(label)}</option>`).join('')}</select>`);
        return `<div class="section-label">SCOPED DEPENDENCY</div>${field('From',`<select data-program-edge="from">${options(edge.from)}</select>`)}${field('To',`<select data-program-edge="to">${options(edge.to)}</select>`)}${choose('Priority','priority',[['required','Required'],['desired','Desired'],['avoided','Avoided']])}${choose('Relationship','relation',[['walking','Walking route'],['adjacency','Shared boundary'],['delivery','Delivery connection'],['transport','Passenger transport']])}${choose('Purpose','purpose',[['crew','Crew'],['items','Items / goods'],['patients','Patients'],['waste','Waste']])}${choose('Direction','direction',[['both','Both directions'],['forward','From → To']])}${field('Weight',`<input data-program-edge="weight" type="number" min="0.1" max="1000" step="0.1" value="${edge.weight}">`)}${field('Maximum distance / avoidance radius',`<input data-program-edge="maxDistance" type="number" min="1" max="100000" value="${edge.maxDistance??''}">`)}${field('Permitted connection methods',`<select data-program-edge="allowedModes" multiple size="5">${[['publicHall','Public hall'],['staffPassage','Internal staff passage'],['maintenance','Maintenance'],['disposalShortcut','Passenger disposal shortcut'],['mail','Item mail']].map(([mode,label])=>`<option value="${mode}" ${(edge.allowedModes||[]).includes(mode)?'selected':''}>${label}</option>`).join('')}</select>`,'Select multiple methods with Ctrl or Cmd. Operational tubes and mail are later stages; Required rules need a constructed method.')}<label class="toggle policy-toggle"><input data-program-edge="strictAvoidance" type="checkbox" ${edge.strictAvoidance?'checked':''}> Never allow this Avoided relationship</label>${field('Description / reason',`<textarea data-program-edge="description" rows="3" maxlength="2000">${esc(edge.description||'')}</textarea>`)}<button class="full danger" data-program-action="remove-relation">Remove dependency</button>`;
    }
    function handleClick(event){
        const t=event.target.closest('[data-program-select],[data-program-relation],[data-program-action]');if(!t)return false;
        if(t.dataset.programSelect){selected=t.dataset.programSelect;relationIndex=-1;redraw();return true;}
        if(t.dataset.programRelation!==undefined){relationIndex=+t.dataset.programRelation;redraw();return true;}
        const {d,s,kind}=locate(),action=t.dataset.programAction,p=project();
        if(action!=='add-relation'&&action!=='remove-relation')relationIndex=-1;
        if(action==='add-department'){
            if(departments().length>=16){notify('The planner supports up to 16 departments.',true);return true;}
            record();const id=unique(departments(),'Department');departments().push({id,description:'',width:14,height:12,minArea:40,maxArea:departments()[0].maxArea,access:'public',exterior:false,entrances:1,color:'#6e90b0',requiredConnections:[],preferredNeighbors:[],grouping:'preferGrouped',allowSplit:true,subdepartments:[]});
            p.topology.nodes.push(id);p.topology.nodePositions||={};p.topology.nodePositions[id]={x:20+departments().length*5,y:20+departments().length*3};selected=id;
        }else if(action==='add-subdepartment'&&d){record();d.subdepartments||=[];const id=unique(d.subdepartments,'Group');d.subdepartments.push({id,name:id,description:'',minArea:25,targetArea:60,maxArea:400,requireInternalAccess:false,allowSplit:true,rooms:[]});selected=d.id+'/'+id;
        }else if(action==='add-room'&&s){record();s.rooms||=[];const id=unique(s.rooms,'Room');s.rooms.push({id,name:id,description:'',kind:'room',minCount:1,maxCount:1,minArea:16,maxArea:120,accessibleFrom:[]});selected=d.id+'/'+s.id+'/'+id;
        }else if(action==='remove-item'&&d){
            if(kind==='department'&&departments().length<=3){notify('Keep at least three departments.',true);return true;}
            record();removeProgramPath(p,selected);
            if(kind==='room'){s.rooms=s.rooms.filter(r=>r.id!==locate().r.id);selected=d.id+'/'+s.id;}
            else if(kind==='subdepartment'){d.subdepartments=d.subdepartments.filter(x=>x!==s);selected=d.id;}
            else{p.config.departments=departments().filter(x=>x!==d);p.topology.nodes=p.topology.nodes.filter(n=>n!==d.id);p.topology.edges=p.topology.edges.filter(e=>e.from!==d.id&&e.to!==d.id);delete p.topology.nodePositions?.[d.id];for(const other of departments()){other.requiredConnections=(other.requiredConnections||[]).filter(id=>id!==d.id);other.preferredNeighbors=(other.preferredNeighbors||[]).filter(id=>id!==d.id);}selected='';}
        }else if(action==='add-relation'){
            const paths=allPaths();if(paths.length<2)return true;record();p.config.hierarchyRelations||=[];p.config.hierarchyRelations.push({id:`hierarchy-${Date.now()}-${p.config.hierarchyRelations.length}`,origin:'custom',from:paths.includes(selected)?selected:paths[0],to:paths.find(path=>path!==selected)||paths[1],relation:'walking',priority:'desired',weight:1,purpose:'crew',direction:'both',allowedModes:['staffPassage','publicHall'],description:'',strictAvoidance:false});relationIndex=p.config.hierarchyRelations.length-1;
        }else if(action==='remove-relation'&&relationIndex>=0){record();p.config.hierarchyRelations.splice(relationIndex,1);relationIndex=-1;
        }else return true;
        update();return true;
    }
    function handleChange(event){
        const t=event.target,{d,s,r,item}=locate();
        if(t.dataset.programEdge){if(t.checkValidity&&!t.checkValidity()){notify('Enter a value inside the field limits.',true);return true;}record();const e=project().config.hierarchyRelations[relationIndex],name=t.dataset.programEdge;if(e)e[name]=name==='allowedModes'?Array.from(t.selectedOptions).map(o=>o.value):name==='maxDistance'&&t.value.trim()===''?null:t.type==='checkbox'?t.checked:t.type==='number'?+t.value:t.value;update();return true;}
        const name=t.dataset.programField;if(!name||!item)return false;
        if(t.checkValidity&&!t.checkValidity()){notify('Enter a valid value inside the field limits.',true);redraw();return true;}
        if(name==='id'){
            const id=t.value.trim(),siblings=r?s.rooms:s?d.subdepartments:departments();
            if(!/^[A-Za-z0-9][A-Za-z0-9 _-]{0,47}$/.test(id)||siblings.some(x=>x!==item&&x.id===id)){notify('Use a unique ID of 1–48 letters, numbers, spaces, underscores or hyphens.',true);redraw();return true;}
            record();const old=selected,oldId=item.id;item.id=id;selected=old.split('/').slice(0,-1).concat(id).join('/');renameProgramPath(project(),old,selected);
            if(!s){project().topology.nodes=project().topology.nodes.map(n=>n===oldId?id:n);project().topology.nodePositions=Object.fromEntries(Object.entries(project().topology.nodePositions||{}).map(([key,pos])=>[key===oldId?id:key,pos]));for(const e of project().topology.edges){if(e.from===oldId||e.to===oldId)e.origin='custom';if(e.from===oldId)e.from=id;if(e.to===oldId)e.to=id;}for(const other of departments()){other.requiredConnections=(other.requiredConnections||[]).map(v=>v===oldId?id:v);other.preferredNeighbors=(other.preferredNeighbors||[]).map(v=>v===oldId?id:v);}}
        }else{record();if(name==='topologyHub'&&t.checked)for(const other of departments())other.topologyHub=false;item[name]=name==='accessibleFrom'?Array.from(t.selectedOptions).map(o=>o.value):t.type==='checkbox'?t.checked:t.type==='number'?+t.value:t.value;}
        update();return true;
    }
    return {inspectorHtml,detailsHtml,handleClick,handleChange,select(path){selected=path;relationIndex=-1;redraw();},reset(){selected='';relationIndex=-1;}};
}
