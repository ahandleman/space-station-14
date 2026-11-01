// Browser-independent check of the real spatial editor event handlers and API.
// Requires the Station Creator server at http://127.0.0.1:8420.
// It does not claim browser layout, actual hit testing, or engine simulation coverage.
import assert from 'node:assert/strict';
import { Blob } from 'node:buffer';
import { webcrypto } from 'node:crypto';

const base='http://127.0.0.1:8420', nativeFetch=globalThis.fetch;
const elements=new Map(),listeners=new Map(),blobs=[];
function matches(element,selectors){
    return selectors.split(',').some(raw=>{
        const selector=raw.trim();
        if(selector.startsWith('#'))return element.id===selector.slice(1);
        const attribute=/^\[data-([\w-]+)\]$/.exec(selector);
        if(attribute)return Object.hasOwn(element.dataset,attribute[1].replace(/-([a-z])/g,(_,c)=>c.toUpperCase()));
        return selector.toLowerCase()===element.tagName.toLowerCase();
    });
}
class Element{
    constructor(id='',dataset={},tagName='button'){
        Object.assign(this,{id,dataset,tagName,innerHTML:'',textContent:'',checked:true,disabled:false,hidden:false,value:'',style:{},classList:{toggle(){}},events:new Map()});
    }
    addEventListener(name,handler){this.events.set(name,handler);}
    setAttribute(){} checkValidity(){return true;}
    closest(selector){return matches(this,selector)?this:null;}
    matches(selector){return matches(this,selector);}
    createSVGPoint(){return{x:0,y:0,matrixTransform(){return{x:this.x,y:this.y};}};}
    getScreenCTM(){return{inverse(){return{};}};}
    setPointerCapture(){} click(){}
}
globalThis.document={
    body:new Element('body',{},'body'),
    getElementById(id){if(!elements.has(id))elements.set(id,new Element(id,{},'input'));return elements.get(id);},
    querySelectorAll(selector){return [...elements.values()].filter(e=>matches(e,selector));},
    addEventListener(name,handler){listeners.set(name,handler);},
    createElement(tagName){return new Element('',{},tagName);}
};
globalThis.window={addEventListener(){}};
globalThis.Blob=Blob;if(!globalThis.crypto)globalThis.crypto=webcrypto;
URL.createObjectURL=blob=>{blobs.push(blob);return'blob:spatial-check';};URL.revokeObjectURL=()=>{};
globalThis.fetch=(url,options)=>nativeFetch(new URL(url,base),options);
const page=await(await nativeFetch(base)).text();
for(const match of page.matchAll(/<([a-z][\w:-]*)\b[^>]*\bid="([^"]+)"/g))elements.set(match[2],new Element(match[2],{},match[1]));
await import(new URL(`../wwwroot/workbench.js?spatial-workflow=${Date.now()}`,import.meta.url));

const el=id=>document.getElementById(id);
const click=(id,dataset={})=>listeners.get('click')({target:new Element(id,dataset)});
const select=id=>click('',{spatialSelect:id});
const tool=id=>click('',{spatialTool:id});
const action=id=>click('',{spatialAction:id});
function change(dataset,value,type='number',checked=true){
    const target=new Element('',dataset,'input');Object.assign(target,{value:String(value),type,checked});listeners.get('change')({target});
}
async function settled(){
    const deadline=Date.now()+120000;
    while(el('generate').disabled&&Date.now()<deadline)await new Promise(resolve=>setTimeout(resolve,10));
    assert.equal(el('generate').disabled,false,'Spatial API operation timed out');
}
async function saved(){click('save-project');return JSON.parse(await blobs.at(-1).text());}
async function stroke(x,y,target=el('canvas'),endX=x,endY=y){
    el('canvas').events.get('pointerdown')({type:'pointerdown',target,button:0,pointerId:1,clientX:x,clientY:y});
    if(endX!==x||endY!==y)el('canvas').events.get('pointermove')({type:'pointermove',target,clientX:endX,clientY:endY});
    await el('canvas').events.get('pointerup')({type:'pointerup',target:el('canvas')});await settled();
}

assert.equal(el('stage-title').textContent,'Topology');
const topology=JSON.stringify((await saved()).topology);
click('topology-next');await settled();
assert.equal(el('stage-title').textContent,'Placement',el('diagnostics').innerHTML);
let p=await saved(),plan=p.spatialPlan;
assert.ok(plan.envelope.length>0&&plan.seeds.length===p.config.departments.length);
assert.ok(plan.publicReservations.length>0&&plan.serviceReservations.length>0);
assert.equal(JSON.stringify(p.topology),topology);

// A captured seed tap selects the inspector; a drag changes the physical position
// independently from the topology diagram and remains undoable.
const originalSeed=plan.seeds.find(s=>s.id==='Medical');
await stroke(originalSeed.position.x,originalSeed.position.y,new Element('',{spatialSeed:'Medical'},'g'));
assert.match(el('topology-details').innerHTML,/Seed X/);
await stroke(originalSeed.position.x,originalSeed.position.y,new Element('',{spatialSeed:'Medical'},'g'),originalSeed.position.x+plan.cellSize,originalSeed.position.y);
p=await saved();assert.notEqual(JSON.stringify(p.spatialPlan.seeds.find(s=>s.id==='Medical').position),JSON.stringify(originalSeed.position));
assert.equal(JSON.stringify(p.topology),topology);click('undo');
assert.equal(JSON.stringify((await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').position),JSON.stringify(originalSeed.position));

select('Medical');change({seedField:'pinned'},'', 'checkbox',true);
assert.equal((await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').pinned,true);
change({seedField:'targetArea'},originalSeed.targetArea+plan.cellSize**2);
assert.equal((await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').targetArea,originalSeed.targetArea+plan.cellSize**2);
click('undo');
const pinnedPosition=(await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').position;
action('placement');await settled();
assert.equal(JSON.stringify((await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').position),JSON.stringify(pinnedPosition));

// Envelope strokes are authoring operations, not just a changed preview. A cut or
// added detached planning patch remains a draft until validated; Undo recovers it.
const beforeEnvelope=await saved();tool('envelope');await stroke(1,1);
p=await saved();assert.equal(p.spatialPlan.envelopeEdited,true);
assert.ok(p.spatialPlan.envelope.some(cell=>cell.x===0&&cell.y===0));
click('undo');assert.equal(JSON.stringify((await saved()).spatialPlan.envelope),JSON.stringify(beforeEnvelope.spatialPlan.envelope));
tool('void');const edgeCell=beforeEnvelope.spatialPlan.envelope[0];await stroke(edgeCell.x+.1,edgeCell.y+.1);
assert.ok((await saved()).spatialPlan.envelope.length<beforeEnvelope.spatialPlan.envelope.length);
click('undo');

click('run-stage');await settled();
assert.equal(el('stage-title').textContent,'Department growth',el('diagnostics').innerHTML);
p=await saved();assert.equal(p.spatialPlan.regions.length,p.config.departments.length);
assert.ok(p.spatialPlan.regions.every(region=>region.territory.length>0&&region.cells.length>0));
assert.ok(p.spatialPlan.publicCorridors.length>0&&p.spatialPlan.ports.length>0);
assert.match(el('canvas').innerHTML,/data-spatial-region="Medical"/);

// Manual territory edits rebuild boundaries through the API, preserving the
// authored allocation rather than running growth again behind the mapper's back.
select('Medical');tool('release');const beforeTerritory=await saved();
const region=beforeTerritory.spatialPlan.regions.find(r=>r.id==='Medical');
await stroke(region.territory[0].x+.1,region.territory[0].y+.1);
let released=await saved();
assert.ok(released.spatialPlan.regions.find(r=>r.id==='Medical').territory.length<region.territory.length);
const remainingKeys=new Set(released.spatialPlan.regions.flatMap(r=>r.territory).map(cell=>`${cell.x},${cell.y}`));
const releasedCells=region.territory.filter(cell=>!remainingKeys.has(`${cell.x},${cell.y}`));
assert.ok(releasedCells.length>0);
const reserveKeys=new Set(released.spatialPlan.structuralReserve.map(cell=>`${cell.x},${cell.y}`));
assert.ok(releasedCells.every(cell=>reserveKeys.has(`${cell.x},${cell.y}`)),'Released territory becomes structural reserve');
click('undo');assert.equal(JSON.stringify((await saved()).spatialPlan.regions.find(r=>r.id==='Medical').territory),JSON.stringify(region.territory));
click('redo');assert.equal(JSON.stringify((await saved()).spatialPlan.regions.find(r=>r.id==='Medical').territory),JSON.stringify(released.spatialPlan.regions.find(r=>r.id==='Medical').territory));
select('Medical');
tool('territory');await stroke(region.territory[0].x+.1,region.territory[0].y+.1);
assert.equal((await saved()).spatialPlan.regions.find(r=>r.id==='Medical').territory.length,region.territory.length);
// Restore a saved growth result through project import and verify the authored
// territory mask survives independently of the editor's current selection/tool.
const fileEvent={target:{files:[{size:JSON.stringify(beforeTerritory).length,text:async()=>JSON.stringify(beforeTerritory)}],value:'saved.json'}};
await el('project-file').events.get('change')(fileEvent);await settled();
assert.equal(JSON.stringify((await saved()).spatialPlan.regions.find(r=>r.id==='Medical').territory),JSON.stringify(region.territory));

click('run-stage');await settled();assert.equal(el('stage-title').textContent,'Maintenance',el('diagnostics').innerHTML);
p=await saved();assert.ok(p.spatialPlan.maintenance.length>0);
assert.ok(p.spatialPlan.deepMaintenanceRooms.length>=p.config.spatial.deepMaintenanceMin);
assert.ok(p.spatialPlan.solarInstallations.length>=p.config.spatial.solarMin);
assert.ok(p.spatialPlan.maintenanceSpaceAccess.length>=p.config.spatial.spaceAccessMin);
assert.match(el('inspector-content').innerHTML,/data-spatial-setting="deepMaintenanceMin"/);
assert.match(el('canvas').innerHTML,/external solar panel reservation/);
assert.ok(p.spatialPlan.hierarchy.roomFeatures.some(r=>r.definition==='Engineering/Atmos/TEG'));
assert.equal(p.spatialPlan.seeds.find(s=>s.id==='Security').allowMaintenanceAccess,false);
const beforeMaintenance=await saved();tool('erase-maintenance');const serviceCell=beforeMaintenance.spatialPlan.maintenance[0];
await stroke(serviceCell.x+.1,serviceCell.y+.1);
assert.ok((await saved()).spatialPlan.maintenance.length<beforeMaintenance.spatialPlan.maintenance.length);
await el('project-file').events.get('change')({target:{files:[{size:JSON.stringify(beforeMaintenance).length,text:async()=>JSON.stringify(beforeMaintenance)}],value:'saved.json'}});
await settled();

click('run-stage');await settled();assert.equal(el('result-badge').textContent,'GEOMETRY CHECKED',el('diagnostics').innerHTML);
p=await saved();assert.match(el('metrics').innerHTML,/SHA256/);assert.equal(JSON.stringify(p.topology),topology);
assert.equal(p.spatialPlan.seeds.find(s=>s.id==='Medical').pinned,true);
const imported=await(await nativeFetch(`${base}/api/import`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(p)})).json();
assert.equal(imported.result.success,true);assert.equal(imported.result.metrics.hash,p.result.metrics.hash);
click('export-svg');assert.match(await blobs.at(-1).text(),/Spatial geometry checked/);

// Upstream dependency edits invalidate derived regions but retain the mapper's
// pinned physical seed and envelope, so iteration does not erase authored placement.
const envelopeBeforeTopology=JSON.stringify(p.spatialPlan.envelope),seedBeforeTopology=JSON.stringify(p.spatialPlan.seeds.find(s=>s.id==='Medical').position);
click('',{stage:'0'});click('',{topologyEdge:p.topology.edges[0].id});
change({dependencyField:'description'},'Keep my pinned medical placement while revising station dependencies.','text');
click('',{topologyAction:'save-edge'});
p=await saved();assert.ok(p.spatialPlan);assert.equal(JSON.stringify(p.spatialPlan.envelope),envelopeBeforeTopology);
assert.equal(JSON.stringify(p.spatialPlan.seeds.find(s=>s.id==='Medical').position),seedBeforeTopology);
assert.equal(p.spatialPlan.seeds.find(s=>s.id==='Medical').pinned,true);assert.equal(p.spatialPlan.regions.length,0);
click('generate');await settled();assert.equal(el('result-badge').textContent,'GEOMETRY CHECKED',el('diagnostics').innerHTML);
assert.equal(JSON.stringify((await saved()).spatialPlan.seeds.find(s=>s.id==='Medical').position),seedBeforeTopology);
console.log('Spatial frontend passed: placement, seed selection/drag/pin/budget, envelope painting, grown masks, manual territory undo/redo, maintenance editing, import/exports, and pinned placement through topology edits.');
