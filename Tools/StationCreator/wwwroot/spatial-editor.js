const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const key = p => `${p.x},${p.y}`;
const clone = value => JSON.parse(JSON.stringify(value));
const field = (title, body, help = '') => `<label class="field">${escape(title)}${body}${help ? `<small class="field-help">${escape(help)}</small>` : ''}</label>`;
const number = (title, value, attributes) => field(title, `<input type="number" value="${value}" ${attributes}>`);
const row = (...fields) => `<div class="field-row">${fields.join('')}</div>`;
const definitions = [
    ['select', '↖ Select / move'], ['envelope', '▧ Add envelope'], ['void', '⌫ Cut space'],
    ['public', '▧ Reserve hall'], ['service', '▧ Reserve service'], ['unreserve', '⌫ Clear reservation'],
    ['territory', '▧ Assign territory'], ['release', '⌫ Release territory'],
    ['maintenance', '▧ Add maintenance'], ['erase-maintenance', '⌫ Erase maintenance']
];

export function createSpatialEditor({ getProject, record, changed, redraw, drawMap, notify, run }) {
    let selected = null;
    let tool = 'select';
    let drag = null;
    let brush = 1;
    let showOwnership = true;
    let showReservations = true;
    let showProgram = true;
    let paintChanged = false;

    const plan = () => getProject().spatialPlan;
    const settings = () => getProject().config.spatial || { cellSize: 5, fillRatio: .78, maintenanceFraction: .30, growthSteps: 100000,deepMaintenanceMin:2,deepMaintenanceMax:4,solarMin:2,solarMax:4,spaceAccessMin:2,spaceAccessMax:3 };
    const cellSize = () => plan()?.cellSize || settings().cellSize;
    const selectedSeed = () => plan()?.seeds?.find(s => s.id === selected);
    const selectedRegion = () => plan()?.regions?.find(r => r.id === selected);
    const definition = id => getProject().config.departments.find(d => d.id === id);
    const snap = point => ({ x: Math.floor(point.x / cellSize()) * cellSize() + Math.floor(cellSize()/2), y: Math.floor(point.y / cellSize()) * cellSize() + Math.floor(cellSize()/2) });

    function reset() { tool = 'select'; drag = null; paintChanged = false; }
    function activate() { reset(); }
    function toolsHtml(stage) {
        const ids = stage === 1 ? ['select','envelope','void','public','service','unreserve'] : stage === 2 ? ['select','territory','release'] : ['select','maintenance','erase-maintenance'];
        return `<div class="section-label">EDITING TOOLS</div><div class="tools spatial-tools">${ids.map(id => `<button data-spatial-tool="${id}" class="${tool===id?'selected':''}">${definitions.find(([name])=>name===id)[1]}</button>`).join('')}</div>${tool !== 'select' ? stage===3?`<p class="field-help">Drag to extend a ${getProject().config.maintenanceWidth}-tile passage. Oversized open pockets are rejected by validation.</p>`:number('Brush size (planning cells)',brush,'id="spatial-brush" min="1" max="4"') : ''}`;
    }
    function departmentList() {
        return `<div class="departments">${getProject().config.departments.map(d => {
            const region=plan()?.regions?.find(r=>r.id===d.id),seed=plan()?.seeds?.find(s=>s.id===d.id);
            return `<button class="department-row ${selected===d.id?'selected':''}" data-spatial-select="${escape(d.id)}"><i style="background:${/^#[0-9a-f]{6}$/i.test(d.color)?d.color:'#6e90b0'}"></i><span>${escape(d.id)}</span><small>${region?`${region.cells.length} tiles`:seed?`${seed.targetArea} target`:''}${seed?.pinned?' · pin':''}</small></button>`;
        }).join('')}</div>`;
    }
    function maintenanceSettings(){
        const s=settings();
        const preference=field('Surplus pocket preference',`<select data-spatial-setting="maintenancePocketPreference"><option value="departments" ${(s.maintenancePocketPreference||'departments')==='departments'?'selected':''}>Grow nearby departments first</option><option value="rooms" ${s.maintenancePocketPreference==='rooms'?'selected':''}>Grow enclosed maintenance rooms first</option></select>`,'Both choices retain count, area, access and passage-width rules.');
        const roomTarget=field('Preferred deep-room floor area',`<input type="number" value="${s.deepMaintenanceRoomTargetArea??49}" data-spatial-setting="deepMaintenanceRoomTargetArea" min="4" max="4096">`,'A soft target within the room limits; smaller rooms can fit when space is constrained.');
        const commonSettings='<details><summary>Hall-connected maintenance rooms</summary>'+row(number('Minimum count',s.commonMaintenanceMin??0,'data-spatial-setting="commonMaintenanceMin" min="0" max="12"'),number('Maximum count',s.commonMaintenanceMax??8,'data-spatial-setting="commonMaintenanceMax" min="0" max="12"'))+row(number('Minimum room area',s.commonMaintenanceRoomMinArea??9,'data-spatial-setting="commonMaintenanceRoomMinArea" min="4" max="4096"'),number('Maximum room area',s.commonMaintenanceRoomMaxArea??300,'data-spatial-setting="commonMaintenanceRoomMaxArea" min="4" max="4096"'))+number('Preferred room area',s.commonMaintenanceRoomTargetArea??49,'data-spatial-setting="commonMaintenanceRoomTargetArea" min="4" max="4096"')+'<p class="field-help">These enclosed rooms have actual doors into both public halls and maintenance. They do not count as deep maints.</p></details>';
        return '<div class="section-label">SERVICE SPACE PROGRAM</div>'+row(number('Passage width (tiles)',getProject().config.maintenanceWidth,'data-spatial-config="maintenanceWidth" min="2" max="3"'),number('Maximum open width',s.maintenancePassageMaxWidth??3,'data-spatial-setting="maintenancePassageMaxWidth" min="2" max="3"'))+row(number('Deep maints · minimum',s.deepMaintenanceMin??2,'data-spatial-setting="deepMaintenanceMin" min="0" max="12"'),number('Maximum',s.deepMaintenanceMax??4,'data-spatial-setting="deepMaintenanceMax" min="0" max="12"'))+row(number('Deep room minimum area',s.deepMaintenanceRoomMinArea??9,'data-spatial-setting="deepMaintenanceRoomMinArea" min="4" max="4096"'),number('Maximum area',s.deepMaintenanceRoomMaxArea??300,'data-spatial-setting="deepMaintenanceRoomMaxArea" min="4" max="4096"'))+row(number('Solar arrays · minimum',s.solarMin??2,'data-spatial-setting="solarMin" min="0" max="12"'),number('Maximum',s.solarMax??4,'data-spatial-setting="solarMax" min="0" max="12"'))+row(number('Space access · minimum',s.spaceAccessMin??2,'data-spatial-setting="spaceAccessMin" min="0" max="12"'),number('Maximum',s.spaceAccessMax??3,'data-spatial-setting="spaceAccessMax" min="0" max="12"'))+roomTarget+preference+commonSettings+'<p class="field-help">Passages stay narrow. Surplus service pockets expand neighboring departments or enclosed, counted deep-maint rooms. Deep rooms require maintenance travel; solar arrays use external catwalks; space frontages retain closed barriers.</p>';
    }
    function inspectorHtml(stage) {
        const c=getProject().config,p=plan(),s=settings();
        let html=`<p class="description">${stage===1?'Shape the station envelope, reserve circulation and service space, then place department seeds. Graph positions and physical seeds are separate.':stage===2?'Departments grow into connected territories around the reserved routes. Inspect usable floor, shared walls and entrances, or edit the territory allocation.':stage===3?'Build service routes through the reserved internal seams. Refine maintenance and inspect declared access points. Secure departments have maintenance access disabled by default.':'Inspect the actual region masks, circulation and maintenance connections before exporting the plan.'}</p>`;
        if(stage===1){
            html+=row(number('Canvas width',c.width,'data-spatial-config="width" min="32" max="256"'),number('Canvas height',c.height,'data-spatial-config="height" min="32" max="256"'));
            html+=row(number('Planning cell size',s.cellSize,'data-spatial-setting="cellSize" min="4" max="8"'),number('Public hall width',c.corridorWidth,'data-spatial-config="corridorWidth" min="2" max="5"'));
            html+=row(number('Department fill target',s.fillRatio,'data-spatial-setting="fillRatio" min="0.45" max="0.95" step="0.01"'),number('Initial service reservation',s.maintenanceFraction,'data-spatial-setting="maintenanceFraction" min="0.10" max="0.50" step="0.01"'));
            html+=maintenanceSettings();
            html+=number('Maximum department boundary depth',s.departmentBoundaryMaxDepth??1,'data-spatial-setting="departmentBoundaryMaxDepth" min="1" max="3"');
            html+='<p class="field-help">Higher fill targets make a more compact envelope. Walls, routes and service space are budgeted alongside usable department floor.</p>';
            html+=`<button class="full" data-spatial-action="placement">${p?'Rebuild placement · keep pinned seeds':'Build placement'}</button>`;
            if(p)html+=`<p class="field-help">Painted envelopes are preserved when placement is rebuilt. Use Reset envelope to return to the topology silhouette.</p>${toolsHtml(stage)}`;
        }else if(stage===2){
            html+=`<button data-spatial-action="growth" class="full">Regrow all departments</button><p class="field-help">Regrowth uses the current envelope, reservations and physical seeds. It replaces manual territory edits.</p>`;
            if(p?.regions?.length)html+=toolsHtml(stage);
        }else if(stage===3){
            html+=maintenanceSettings();
            html+=`<button data-spatial-action="maintenance" class="full">Rebuild maintenance routes</button><p class="field-help">Rebuilding replaces manual maintenance edits. It uses internal service reservations and preserves grown departments.</p>`;
            if(p?.regions?.length)html+=toolsHtml(stage);
        }else{
            html+='<div class="notice">Checks cover department and room footprints, internal access, circulation, maintenance-only rooms, external solar connections and dependency fulfillment.</div>';
        }
        if(p){
            if(p.regions?.length)html+=`<label class="toggle policy-toggle"><input id="spatial-program" type="checkbox" ${showProgram?'checked':''}> Show subdepartments and planned rooms</label>`;
            html+=`<div class="section-label">DEPARTMENTS</div>${departmentList()}<details><summary>View overlays</summary><label class="toggle policy-toggle"><input id="spatial-ownership" type="checkbox" ${showOwnership?'checked':''}> Show territory ownership</label><label class="toggle policy-toggle"><input id="spatial-reservations" type="checkbox" ${showReservations?'checked':''}> Show route reservations</label></details>`;
            html+=`<details><summary>Generation limits</summary>${number('Growth work budget',s.growthSteps,'data-spatial-setting="growthSteps" min="100" max="1000000" step="100"')}${number('Candidate attempts',c.candidateBudget,'data-spatial-config="candidateBudget" min="1" max="16"')}</details>`;
        }
        return html;
    }
    function detailsHtml(stage) {
        const p=plan(),seed=selectedSeed(),region=selectedRegion(),d=definition(selected);
        if(!p)return '<p class="notice">Build placement to create a station envelope and physical department seeds.</p>';
        let html='';
        if(d&&seed){
            html+=`<div class="section-label">${escape(d.id)}</div><p class="muted">${escape(d.description||`${d.access} department`)}</p>`;
            if(stage===1){
                html+=row(number('Seed X',seed.position.x,'data-seed-position="x" min="0" max="255"'),number('Seed Y',seed.position.y,'data-seed-position="y" min="0" max="255"'));
                html+=number('Target usable floor area',seed.targetArea,`data-seed-field="targetArea" min="${d.minArea}" max="${d.maxArea}"`);
                html+=`<label class="toggle policy-toggle"><input data-seed-field="pinned" type="checkbox" ${seed.pinned?'checked':''}> Keep this seed when replacing placement</label><p class="field-help">Dragging always changes the physical seed. Pinning protects its position from automatic replacement.</p>`;
                html+=row(number('Minimum floor area',d.minArea,'data-region-definition="minArea" min="1" max="4000"'),number('Maximum floor area',d.maxArea,'data-region-definition="maxArea" min="1" max="8000"'));
            }else if(region){
                const floor=region.cells.length,territory=region.territory.length;
                const exterior=getProject().result?.metrics?.exteriorFrontageByDepartment?.[d.id];
                html+=`<div class="region-facts"><span>Usable floor<strong>${floor.toLocaleString()} tiles</strong></span><span>Ownership area<strong>${territory.toLocaleString()} tiles</strong></span><span>Floor target<strong>${seed.targetArea.toLocaleString()} tiles</strong></span><span>Allowed floor area<strong>${d.minArea}–${d.maxArea}</strong></span>${exterior!=null?`<span>Space-facing frontage<strong>${exterior} boundary tiles</strong></span>`:''}</div>`;
            }
            html+=`<label class="toggle policy-toggle"><input data-seed-field="allowMaintenanceAccess" type="checkbox" ${seed.allowMaintenanceAccess?'checked':''}> Allow declared maintenance entrance</label><p class="field-help">A service entrance inherits this department's ${escape(d.access)} access policy. Painting a route alone does not authorize a wall opening.</p>`;
            const ports=(p.ports||[]).filter(port=>port.department===d.id);
            if(ports.length)html+=`<div class="section-label">DECLARED ENTRANCES</div>${ports.map(port=>`<div class="spatial-port"><b>${escape(port.kind)}</b><span>${escape(port.access)} · ${port.width} wide · (${port.center.x}, ${port.center.y})${port.otherDepartment?` → ${escape(port.otherDepartment)}`:''}</span></div>`).join('')}`;
        }else html+='<p class="notice">Select a department seed or grown region to inspect its area and access points.</p>';
        if(stage===1)html+=`<div class="separator"></div><button data-spatial-action="reset-envelope" class="full">Reset envelope to topology silhouette</button><p class="field-help">Space outside the envelope stays exterior. Inside it, floor, routes, walls and reserved structure are accounted for separately.</p>`;
        if(stage===2)html+='<p class="field-help">Assign territory to the selected department, or release it to the structural reserve. Circulation reservations stay protected. Boundaries and ports are rebuilt after each stroke.</p>';
        const hierarchy=p.hierarchy;
        if(d&&hierarchy){const zones=(hierarchy.subdepartments||[]).filter(s=>s.department===d.id||s.hostDepartment===d.id);if(zones.length)html+=`<div class="section-label">SUBDEPARTMENT ALLOCATIONS</div><div class="installation-list">${zones.map(s=>`<p>${escape(s.id)}<small>${s.cells.length} ownership tiles · ${s.isSatellite?'satellite hosted by '+escape(s.hostDepartment):'inside '+escape(s.department)}</small></p>`).join('')}</div>`;const rooms=(hierarchy.roomFeatures||[]).filter(r=>r.department===d.id||r.hostDepartment===d.id);if(rooms.length)html+=`<div class="section-label">PLANNED ROOMS / FEATURES</div><div class="installation-list">${rooms.map(r=>`<p>${escape(r.definition.split('/').at(-1))}<small>${r.cells.length} clear floor tiles · ${r.kind}${r.accessRoutes.length?' · access: '+r.accessRoutes.map(a=>escape(a.from)).join(', '):''}</small></p>`).join('')}</div>`;}
        if(stage>=3){html+=`<div class="section-label">MAINTENANCE INSTALLATIONS</div><div class="installation-list">${(p.deepMaintenanceRooms||[]).map(r=>`<p>${escape(r.id)}<small>${r.cells.length} floor tiles · maintenance-only door</small></p>`).join('')}${(p.solarInstallations||[]).map(s=>`<p>${escape(s.id)}<small>${s.cells.length} panel reservation tiles · ${s.catwalk.length} exterior catwalk tiles</small></p>`).join('')}${(p.maintenanceSpaceAccess||[]).map((a,i)=>`<p>${escape(a.id)}${field('Sealed space barrier',`<select data-space-barrier="${i}"><option value="window" ${a.barrier==='window'?'selected':''}>Window</option><option value="wall" ${a.barrier==='wall'?'selected':''}>Wall</option></select>`)}</p>`).join('')}</div>`;}
        if(stage>=3&&p.commonMaintenanceRooms?.length)html+=`<div class="section-label">HALL-CONNECTED MAINTENANCE ROOMS</div><div class="installation-list">${p.commonMaintenanceRooms.map(r=>`<p>${escape(r.id)}<small>${r.cells.length} floor tiles · public hall door and maintenance door · enclosed</small></p>`).join('')}</div>`;
        if(stage===3)html+='<p class="field-help">Maintenance must remain inside the envelope, avoid department floors and undeclared openings, and reach public circulation through its passages or declared hall-connected rooms.</p>';
        return html;
    }
    function summaryHtml() {
        const p=plan();if(!p)return '';
        const floor=(p.regions||[]).reduce((sum,r)=>sum+r.cells.length,0);
        const metrics=getProject().result?.metrics;
        const values=[[p.envelope.length,'ENVELOPE TILES'],[floor,'DEPARTMENT FLOOR'],[p.publicCorridors.length||p.publicReservations.length,'PUBLIC / RESERVED'],[p.maintenance.length||p.serviceReservations.length,'MAINT / RESERVED'],[p.structuralReserve.length,'STRUCTURAL RESERVE'],[p.ports.length,'DECLARED PORTS']];
        if(p.deepMaintenanceRooms?.length||p.solarInstallations?.length)values.push([p.deepMaintenanceRooms.length,'DEEP MAINT ROOMS'],[p.commonMaintenanceRooms?.length||0,'HALL-CONNECTED MAINT ROOMS'],[p.solarInstallations.length,'SOLAR ARRAYS'],[p.maintenanceSpaceAccess.length,'SPACE ACCESS AREAS']);
        if(p.hierarchy)values.push([p.hierarchy.subdepartments?.length||0,'SUBDEPARTMENT AREAS'],[p.hierarchy.roomFeatures?.length||0,'ROOM / FEATURE RESERVATIONS']);
        if(metrics?.hash)values.push([metrics.nonRectangularRegions,'NONRECTANGULAR REGIONS'],[metrics.maintenanceAccessPoints,'MAINTENANCE JUNCTIONS'],[metrics.longestEntranceTravel,'MAX ENTRANCE TRAVEL'],[metrics.unallocatedArea,'UNALLOCATED TILES']);
        return `<div class="metrics-grid">${values.map(([v,name])=>`<div class="metric"><strong>${v.toLocaleString()}</strong><span>${name}</span></div>`).join('')}</div>`;
    }
    function graphHtml(layer,stage=2) {
        const p=plan();if(!p)return '';
        const floorStage=stage!==1&&!!p.regions.length;
        let html=layer(p.envelope,'#23323b','envelope');
        if(p.structuralReserve?.length)html+=layer(p.structuralReserve,'#2a3941','structural');
        if(showOwnership&&floorStage){
            for(const r of p.regions){const d=definition(r.id),color=/^#[0-9a-f]{6}$/i.test(d?.color)?d.color:'#6e90b0';html+=`<g data-spatial-region="${escape(r.id)}" class="spatial-region ${selected===r.id?'selected':''}">${layer(r.territory,color,`territory-${r.id}`,.25,false)}</g>`;}
        }
        if(!floorStage||showReservations){html+=layer(p.publicReservations,'#4a6970','public-reservation',.7);html+=layer(stage===1&&p.initialServiceReservations?.length?p.initialServiceReservations:p.serviceReservations,'#515a3c','service-reservation',.65);}
        if(floorStage){
            html+=layer(p.walls,'#536472','walls');
            for(const r of p.regions){const d=definition(r.id),color=/^#[0-9a-f]{6}$/i.test(d?.color)?d.color:'#6e90b0';html+=`<g data-spatial-region="${escape(r.id)}" class="spatial-region ${selected===r.id?'selected':''}">${layer(r.cells,color,`floor-${r.id}`,.8,false)}</g>`;}
            html+=layer(p.publicCorridors,'#ced6cd','public');
            html+=layer(p.maintenance,'#798064','maintenance');
            html+=layer(p.hull,'#91a2af','hull');
            for(const room of p.deepMaintenanceRooms||[]){html+=`<g><title>${escape(room.id)} · maintenance-only room</title>${layer(room.cells,'#a3a277',room.id)}${layer(room.walls,'#586459',room.id+'-walls')}${layer(room.portCells,'#e0bd75',room.id+'-door')}</g>`;const center=labelCenter(room.cells,{x:0,y:0});html+=`<text x="${center.x+.5}" y="${center.y+.5}" text-anchor="middle" font-size="1.05" fill="#e9edcf" pointer-events="none">DEEP</text>`;}
            for(const room of p.commonMaintenanceRooms||[]){html+=`<g><title>${escape(room.id)} · enclosed room with hall and maintenance entrances</title>${layer(room.cells,'#8e957d',room.id)}${layer(room.walls,'#586459',room.id+'-walls')}${layer(room.publicDoorCells,'#69e3cd',room.id+'-hall-door')}${layer(room.maintenanceDoorCells,'#e0bd75',room.id+'-service-door')}</g>`;const center=labelCenter(room.cells,{x:0,y:0});html+=`<text x="${center.x+.5}" y="${center.y+.5}" text-anchor="middle" font-size=".8" fill="#e9edcf" pointer-events="none">MAINT</text>`;}
            for(const solar of p.solarInstallations||[])html+=`<g><title>${escape(solar.id)} · external solar panel reservation, maintenance catwalk access</title>${layer(solar.cells,'#517faf',solar.id)}${layer(solar.catwalk,'#91a7ac',solar.id+'-catwalk')}${layer(solar.connectionCells,'#caaf81',solar.id+'-planned-airlock')}</g>`;
            for(const access of p.maintenanceSpaceAccess||[])html+=`<g><title>${escape(access.id)} · closed ${escape(access.barrier)} frontage to space</title>${layer(access.barrierCells,access.barrier==='window'?'#7edce3':'#cad2d1',access.id)}</g>`;
            if(showProgram&&p.hierarchy){
                for(const sub of p.hierarchy.subdepartments||[]){const cells=new Set(sub.cells.map(key));html+=`<g fill="none" stroke="#e6efdb" stroke-opacity=".55" stroke-width=".14" pointer-events="none"><title>${escape(sub.id)}${sub.isSatellite?' · satellite hosted by '+escape(sub.hostDepartment):''}</title>`;for(const cell of sub.cells)for(const [dx,dy,x1,y1,x2,y2]of [[0,-1,0,0,1,0],[1,0,1,0,1,1],[0,1,0,1,1,1],[-1,0,0,0,0,1]])if(!cells.has(`${cell.x+dx},${cell.y+dy}`))html+=`<path d="M${cell.x+x1},${cell.y+y1}L${cell.x+x2},${cell.y+y2}"/>`;html+='</g>';if(sub.cells.length){const center=labelCenter(sub.cells,sub.seed);html+=`<text x="${center.x+.5}" y="${center.y+.5}" text-anchor="middle" font-size="1.05" fill="#ffffff" style="paint-order:stroke;stroke:#172630;stroke-width:.35px" pointer-events="none">${escape(sub.id.split('/').at(-1))}</text>`;}}
                for(const room of p.hierarchy.roomFeatures||[]){html+=`<g><title>${escape(room.definition)} · ${room.kind} · ${room.cells.length} floor tiles</title>${layer(room.cells,'#cbd7c3',room.id+'-room',.24)}${layer(room.walls,'#31434d',room.id+'-partitions')}${layer(room.doors,'#f1d798',room.id+'-doors')}</g>`;if(room.cells.length){const center=labelCenter(room.cells,room.cells[0]),name=room.definition.split('/').at(-1),width=Math.max(...room.cells.map(c=>c.x))-Math.min(...room.cells.map(c=>c.x))+1,size=Math.min(.85,Math.max(.45,width*1.5/name.length));html+=`<text x="${center.x+.5}" y="${center.y+.5}" text-anchor="middle" font-size="${size}" fill="#eff4df" style="paint-order:stroke;stroke:#172630;stroke-width:.25px" pointer-events="none">${escape(name)}</text>`;}}
            }
            html+=layer(p.reservedExterior,'url(#dock-hatch)','dock-clearance');
            for(const port of p.ports){html+=`<g><title>${escape(port.department)} · ${escape(port.kind)} · ${escape(port.access)}</title>${layer(port.cells,port.kind==='maintenance'?'#e0bd75':'#69e3cd',`port-${port.department}-${port.kind}`,1,false)}</g>`;}
        }
        for(const seed of p.seeds){
            const d=definition(seed.id),region=p.regions.find(r=>r.id===seed.id);
            const seedColor=/^#[0-9a-f]{6}$/i.test(d?.color)?d.color:'#8fb9ab';
            const center=region?.cells.length?labelCenter(region.cells,seed.position):seed.position;
            if(!floorStage){
                const w=d?.width||12,h=d?.height||10;
                html+=`<rect x="${seed.position.x-w/2}" y="${seed.position.y-h/2}" width="${w}" height="${h}" class="planning-box" fill="none" stroke="${seedColor}" stroke-width=".25" stroke-dasharray="1 1" opacity=".6" pointer-events="none"/>`;
                html+=`<g data-spatial-seed="${escape(seed.id)}" class="spatial-seed ${selected===seed.id?'selected':''}" tabindex="0" role="button" aria-label="Move ${escape(seed.id)} seed"><circle cx="${seed.position.x}" cy="${seed.position.y}" r="2.1" fill="${selected===seed.id?'#d5f0d7':'#16252d'}" stroke="${seedColor}" stroke-width=".65"/><path d="M${seed.position.x-3},${seed.position.y}h6 M${seed.position.x},${seed.position.y-3}v6" stroke="#bfd3d1" stroke-width=".22"/><title>${escape(seed.id)}${seed.pinned?' · pinned':''}</title></g>`;
            }
            html+=`<g pointer-events="none" class="spatial-label"><text x="${center.x}" y="${center.y+(floorStage?0:5.4)}" text-anchor="middle" fill="#e8f0e8" font-size="2.05" style="paint-order:stroke;stroke:#172630;stroke-width:.6px">${escape(seed.id)}${seed.pinned?' •':''}</text><text x="${center.x}" y="${center.y+(floorStage?2.5:7.8)}" text-anchor="middle" fill="#bad0d6" font-size="1.35">${region?`${region.cells.length} floor tiles`:`${seed.targetArea} floor target`}</text></g>`;
        }
        return html;
    }
    function labelCenter(cells,fallback) {
        const x=cells.reduce((s,p)=>s+p.x,0)/cells.length,y=cells.reduce((s,p)=>s+p.y,0)/cells.length;
        return cells.reduce((best,p)=>(p.x-x)**2+(p.y-y)**2<(best.x-x)**2+(best.y-y)**2?p:best,cells[0]||fallback);
    }
    function handleClick(event) {
        const target=event.target.closest('[data-spatial-select],[data-spatial-tool],[data-spatial-action]');if(!target)return false;
        if(target.dataset.spatialSelect){selected=target.dataset.spatialSelect;redraw();return true;}
        if(target.dataset.spatialTool){tool=target.dataset.spatialTool;redraw();return true;}
        const action=target.dataset.spatialAction;
        if(action==='reset-envelope'){record();plan().envelopeEdited=false;run('placement',{record:false});}
        else run(action);
        return true;
    }
    function handleChange(event,stage) {
        const target=event.target,p=plan(),seed=selectedSeed();
        if(target.id==='spatial-brush'){brush=Math.max(1,Math.min(4,+target.value||1));return true;}
        if(target.id==='spatial-ownership'){showOwnership=target.checked;drawMap();return true;}
        if(target.id==='spatial-reservations'){showReservations=target.checked;drawMap();return true;}
        if(target.id==='spatial-program'){showProgram=target.checked;drawMap();return true;}
        if(target.dataset.spaceBarrier!==undefined){record();p.maintenanceSpaceAccess[+target.dataset.spaceBarrier].barrier=target.value;changed('presentation');redraw();return true;}
        if(target.dataset.spatialConfig||target.dataset.spatialSetting){
            if(!target.checkValidity()){notify('Enter a value inside the field limits.',true);return true;}
            record();
            if(target.dataset.spatialConfig)getProject().config[target.dataset.spatialConfig]=+target.value;
            else {getProject().config.spatial||={};getProject().config.spatial[target.dataset.spatialSetting]=target.dataset.spatialSetting==='maintenancePocketPreference'?target.value:+target.value;}
            const parameter=target.dataset.spatialConfig||target.dataset.spatialSetting;
            if(['width','height','cellSize'].includes(parameter)){
                // A different raster scale requires a new mask; Undo keeps this reversible.
                getProject().spatialPlan=null;getProject().layout=null;changed('settings');
                notify('Planning scale changed. Build placement again; Undo restores the previous masks.');
            }else if(parameter==='corridorWidth')changed('placement');
            else if(parameter==='maintenanceWidth'){
                changed('presentation');notify('Passage width saved. Rebuild maintenance to apply its width and access ports.');
            }else{
                changed('presentation');
                if(['deepMaintenanceMin','deepMaintenanceMax','commonMaintenanceMin','commonMaintenanceMax','solarMin','solarMax','spaceAccessMin','spaceAccessMax'].includes(parameter))notify('Count bounds saved. Rebuild maintenance to apply the installation program.');
                if(['maintenancePassageMaxWidth','deepMaintenanceRoomMinArea','deepMaintenanceRoomMaxArea','deepMaintenanceRoomTargetArea','commonMaintenanceRoomMinArea','commonMaintenanceRoomMaxArea','commonMaintenanceRoomTargetArea','maintenancePocketPreference'].includes(parameter))notify('Service-space rule saved. Rebuild maintenance to apply it.');
                if(parameter==='departmentBoundaryMaxDepth')notify('Boundary rule saved. Rebuild placement and maintenance to apply it.');
                if(['fillRatio','maintenanceFraction'].includes(parameter))notify('Setting saved. Rebuild placement to apply the envelope and service budgets.');
            }
            redraw();return true;
        }
        if(target.dataset.seedPosition&&seed){record();seed.position[target.dataset.seedPosition]=+target.value;seed.coreFootprint=[];changed('placement');redraw();return true;}
        if(target.dataset.seedField&&seed){
            record();const field=target.dataset.seedField;seed[field]=target.type==='checkbox'?target.checked:+target.value;
            if(field==='pinned'){changed('presentation');redraw();}
            else if(field==='allowMaintenanceAccess'){changed(stage>=3?'maintenance':'placement');redraw();if(stage>=3)run('boundaries',{record:false});}
            else{changed('placement');redraw();}return true;
        }
        if(target.dataset.regionDefinition&&seed){record();const d=definition(seed.id);d[target.dataset.regionDefinition]=+target.value;if(d.minArea<=d.maxArea)seed.targetArea=Math.max(d.minArea,Math.min(d.maxArea,seed.targetArea));changed('placement');redraw();return true;}
        return false;
    }
    function brushCells(point) {
        const size=cellSize(),x=Math.floor(point.x/size)*size,y=Math.floor(point.y/size)*size;
        const cells=[];for(let yy=y;yy<y+size*brush;yy++)for(let xx=x;xx<x+size*brush;xx++)cells.push({x:xx,y:yy});return cells;
    }
    function paint(point,stage,refresh=true) {
        const p=plan(),c=getProject().config;if(!p)return;
        if(stage===1&&p.initialServiceReservations?.length)p.serviceReservations=clone(p.initialServiceReservations);
        const stamp=stage===3?Array.from({length:c.maintenanceWidth*c.maintenanceWidth},(_,i)=>({x:Math.floor(point.x)+i%c.maintenanceWidth,y:Math.floor(point.y)+Math.floor(i/c.maintenanceWidth)})):brushCells(point);
        const cells=stamp.filter(q=>q.x>=0&&q.y>=0&&q.x<c.width&&q.y<c.height);
        const keys=new Set(cells.map(key));
        const replace=(list,add,remove=keys)=>[...list.filter(q=>!remove.has(key(q))),...add];
        const envelope=new Set(p.envelope.map(key));
        if(stage===1){
            if(tool==='envelope'){p.envelope=replace(p.envelope,cells);p.envelopeEdited=true;}
            else if(tool==='void'){p.envelope=p.envelope.filter(q=>!keys.has(key(q)));p.publicReservations=p.publicReservations.filter(q=>!keys.has(key(q)));p.serviceReservations=p.serviceReservations.filter(q=>!keys.has(key(q)));p.envelopeEdited=true;}
            else if(tool==='public'||tool==='service'){
                const valid=cells.filter(q=>envelope.has(key(q)));
                const own=tool==='public'?'publicReservations':'serviceReservations',other=tool==='public'?'serviceReservations':'publicReservations';
                p[own]=replace(p[own],valid);p[other]=p[other].filter(q=>!keys.has(key(q)));
            }else if(tool==='unreserve'){p.publicReservations=p.publicReservations.filter(q=>!keys.has(key(q)));p.serviceReservations=p.serviceReservations.filter(q=>!keys.has(key(q)));}
            p.initialServiceReservations=clone(p.serviceReservations);changed('placement');
        }else if(stage===2){
            const region=selectedRegion();if(!region){notify('Select the department whose territory you want to edit.',true);return;}
            const blocked=new Set([...p.publicReservations,...p.serviceReservations,...p.reservedExterior].map(key));
            const valid=cells.filter(q=>envelope.has(key(q))&&!blocked.has(key(q)));
            const validKeys=new Set(valid.map(key));
            if(tool==='territory'){for(const r of p.regions)r.territory=r.territory.filter(q=>!validKeys.has(key(q)));region.territory.push(...valid);}
            else if(tool==='release')region.territory=region.territory.filter(q=>!keys.has(key(q)));
            changed('growth');
        }else if(stage===3){
            const blocked=new Set([...p.walls,...p.hull,...p.publicCorridors,...p.ports.flatMap(port=>port.cells),...p.regions.flatMap(r=>r.cells),...(p.deepMaintenanceRooms||[]).flatMap(r=>[...r.cells,...r.walls,...r.portCells]),...(p.commonMaintenanceRooms||[]).flatMap(r=>[...r.cells,...r.walls,...r.publicDoorCells,...r.maintenanceDoorCells]),...p.reservedExterior].map(key));
            if(tool==='maintenance'){
                const valid=cells.filter(q=>envelope.has(key(q))&&!blocked.has(key(q)));
                p.maintenance=replace(p.maintenance,valid);
                p.serviceReservations=replace(p.serviceReservations,valid,new Set(valid.map(key)));
            }
            else if(tool==='erase-maintenance')p.maintenance=p.maintenance.filter(q=>!keys.has(key(q)));
            changed('maintenance');
        }
        paintChanged=true;if(refresh)drawMap();
    }
    function pointerDown(event,point,stage) {
        if(!plan())return false;
        if(tool!=='select'&&stage<=3){record();drag={kind:'paint',last:point};paintChanged=false;paint(point,stage);return true;}
        const node=event.target.closest('[data-spatial-seed]');
        if(node&&stage===1){const id=node.dataset.spatialSeed;const seed=plan().seeds.find(s=>s.id===id);drag={kind:'seed',id,start:point,position:clone(seed.position),moved:false};return true;}
        return false;
    }
    function pointerMove(point,stage) {
        if(!drag)return false;
        if(drag.kind==='paint'){
            if(stage===3){const previous=drag.last,dx=point.x-previous.x,dy=point.y-previous.y,steps=Math.ceil(Math.max(Math.abs(dx),Math.abs(dy)));for(let i=1;i<=steps;i++)paint({x:previous.x+dx*i/steps,y:previous.y+dy*i/steps},stage,false);drag.last=point;drawMap();}
            else paint(point,stage);return true;
        }
        const dx=point.x-drag.start.x,dy=point.y-drag.start.y;
        if(!drag.moved&&Math.abs(dx)+Math.abs(dy)<1)return true;
        if(!drag.moved){record();drag.moved=true;}
        const seed=plan().seeds.find(s=>s.id===drag.id);selected=seed.id;
        seed.position=snap({x:drag.position.x+dx,y:drag.position.y+dy});
        seed.coreFootprint=[];
        changed('placement');drawMap();return true;
    }
    async function pointerUp(stage,cancelled=false) {
        if(!drag)return false;const previous=drag;drag=null;
        if(previous.kind==='seed'&&!previous.moved&&!cancelled){selected=previous.id;redraw();}
        if(previous.kind==='paint'&&paintChanged&&!cancelled&&stage>=2){paintChanged=false;await run('boundaries',{record:false});}
        redraw();return true;
    }
    function selectMap(event) {
        const target=event.target.closest('[data-spatial-region],[data-spatial-seed]');
        if(!target)return false;selected=target.dataset.spatialRegion||target.dataset.spatialSeed;redraw();return true;
    }
    return { inspectorHtml,detailsHtml,summaryHtml,graphHtml,handleClick,handleChange,pointerDown,pointerMove,pointerUp,selectMap,reset,activate,
        help(stage){return tool==='select'?(stage===1?'Drag physical seeds · select a department · scroll to zoom':'Select a region to inspect · drag empty space to pan'):`${definitions.find(([id])=>id===tool)?.[1]} · ${stage===3?getProject().config.maintenanceWidth+'-tile passage brush':cellSize()+'-tile planning grid'} · Ctrl+Z to undo`;},
        hasPaintTool(){return tool!=='select';}
    };
}
