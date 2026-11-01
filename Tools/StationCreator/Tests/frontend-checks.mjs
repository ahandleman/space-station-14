// Browser-independent smoke check: real frontend handlers and C# API, minimal DOM.
// Does not verify browser rendering. Run with the server on 127.0.0.1:8420:
// node Tests/frontend-checks.mjs
import assert from 'node:assert/strict';
import { Blob } from 'node:buffer';
import { webcrypto } from 'node:crypto';

const base = 'http://127.0.0.1:8420';
const nativeFetch = globalThis.fetch;
const elements = new Map(), listeners = new Map(), blobs = [], requests = [];
function matches(element, selectors) {
    return selectors.split(',').some(raw => {
        const selector = raw.trim();
        if (selector.startsWith('#')) return element.id === selector.slice(1);
        const attribute = /^\[data-([\w-]+)\]$/.exec(selector);
        if (attribute) {
            const name = attribute[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase());
            return Object.hasOwn(element.dataset, name);
        }
        return selector.toLowerCase() === element.tagName.toLowerCase();
    });
}
class Element {
    constructor(id = '', dataset = {}, tagName = 'button') {
        Object.assign(this, {id, dataset, tagName, innerHTML:'', textContent:'', checked:true,
            disabled:false, hidden:false, value:'', style:{}, classList:{toggle(){}}, events:new Map()});
    }
    addEventListener(name, handler) { this.events.set(name, handler); }
    setAttribute() {}
    checkValidity() { return true; }
    closest(selector) { return matches(this, selector) ? this : null; }
    matches(selector) { return matches(this, selector); }
    createSVGPoint() { return {x:0,y:0,matrixTransform(){return {x:this.x,y:this.y};}}; }
    getScreenCTM() { return {inverse(){return {};}}; }
    setPointerCapture() {}
    click() {}
}
globalThis.document = {
    body: new Element('body', {}, 'body'),
    getElementById(id) {
        if (!elements.has(id)) elements.set(id, new Element(id, {}, 'input'));
        return elements.get(id);
    },
    querySelectorAll(selector) { return [...elements.values()].filter(e => matches(e, selector)); },
    addEventListener(name, handler) { listeners.set(name, handler); },
    dispatchEvent(event) { listeners.get(event.type)?.(event); },
    createElement(tagName) { return new Element('', {}, tagName); }
};
globalThis.window = { addEventListener() {} };
globalThis.Blob = Blob;
if (!globalThis.crypto) globalThis.crypto = webcrypto;
URL.createObjectURL = blob => { blobs.push(blob); return 'blob:frontend-check'; };
URL.revokeObjectURL = () => {};
globalThis.fetch = (url, options) => {
    if (options?.body) requests.push({url:String(url), body:JSON.parse(options.body)});
    return nativeFetch(new URL(url, base), options);
};
const page = await (await nativeFetch(base)).text();
for (const match of page.matchAll(/<([a-z][\w:-]*)\b[^>]*\bid="([^"]+)"/g))
    elements.set(match[2], new Element(match[2], {}, match[1]));
for (const id of ['topology-next','topology-details','topology-legend','map-legend','graph-toggle','grid-toggle','review-title'])
    assert.ok(elements.has(id), `Missing required GUI element: ${id}`);
await import(new URL(`../wwwroot/workbench.js?workflow=${Date.now()}`, import.meta.url));

const el = id => document.getElementById(id);
const click = (id, dataset={}) => listeners.get('click')({target:new Element(id,dataset)});
const action = topologyAction => click('',{topologyAction});
const change = (dataset,value,id='',type='text',checked=true) => {
    const target = new Element(id,dataset,'input');
    Object.assign(target,{value:String(value),type,checked}); listeners.get('change')({target});
};
const field = (name,value,type='text') => change({dependencyField:name},value,`edge-${name}`,type);
const mode = (name,checked) => change({dependencyMode:name},'','','checkbox',checked);
function dragNode(id, dx, dy) {
    const target = new Element('',{topologyNode:id},'g');
    el('canvas').events.get('pointerdown')({type:'pointerdown',target,button:0,pointerId:1,clientX:20,clientY:20});
    el('canvas').events.get('pointermove')({type:'pointermove',target,clientX:20+dx,clientY:20+dy});
    el('canvas').events.get('pointerup')({type:'pointerup',target});
}
function tapCapturedNode(id, eventKind='pointerup') {
    const target=new Element('',{topologyNode:id},'g');
    el('canvas').events.get('pointerdown')({type:'pointerdown',target,button:0,pointerId:1,clientX:20,clientY:20});
    // Pointer capture can retarget release to the SVG rather than its original node.
    el('canvas').events.get(eventKind)({type:eventKind,target:el('canvas')});
}
async function saved() { click('save-project'); return JSON.parse(await blobs.at(-1).text()); }
async function settled() {
    const deadline=Date.now()+120000;
    while(el('generate').disabled && Date.now()<deadline) await new Promise(resolve=>setTimeout(resolve,10));
    assert.equal(el('generate').disabled,false,'API operation timed out');
}

assert.equal(el('stage-title').textContent,'Topology');
assert.match(el('canvas').innerHTML,/class="topology-node/);
for(const priority of ['Required','Desired','Avoided']) assert.ok(el('topology-details').innerHTML.includes(priority));
const original=await saved();

// A normal graph-node press/release must select it even if pointer capture retargets
// the release. A canceled press must not select a department.
action('new-edge'); tapCapturedNode('Science','pointercancel');
assert.ok(!el('topology-details').innerHTML.includes('id="topology-node-name"'));
tapCapturedNode('Science');
assert.match(el('topology-details').innerHTML,/id="topology-node-name" value="Science"/);
assert.match(el('topology-details').innerHTML,/Description \/ role/);

// Deleting a generated dependency is remembered across same/new preset application.
// Only explicit replacement restores the omitted template connection.
const templateEdge=original.topology.edges.find(e=>e.origin==='template'&&e.priority==='required'&&(e.from==='Commons'||e.to==='Commons'));
assert.ok(templateEdge);
click('',{deleteDependency:templateEdge.id});
assert.ok((await saved()).topology.suppressedTemplateIds.includes(templateEdge.id));
click('',{topologyAction:'archetype',value:original.config.archetype}); await settled();
assert.ok(!(await saved()).topology.edges.some(e=>e.id===templateEdge.id));
click('',{topologyAction:'archetype',value:'hub'}); await settled();
assert.ok(!(await saved()).topology.edges.some(e=>e.id===templateEdge.id));
action('reset-template'); await settled();
const reset=await saved(); assert.ok(reset.topology.edges.some(e=>e.id===templateEdge.id));
assert.equal(reset.topology.suppressedTemplateIds.length,0);
for(let i=0;i<4;i++)click('undo');
assert.equal((await saved()).topology.edges.length,original.topology.edges.length);

// Renaming a department converts its existing template relationships into authored
// dependencies, which must survive later preset changes with their IDs preserved.
const scienceEdges=original.topology.edges.filter(e=>e.from==='Science'||e.to==='Science');
assert.ok(scienceEdges.length);
tapCapturedNode('Science'); el('topology-node-name').value='Research';
el('topology-node-description').value='Research department with mapper-owned relationships.'; action('save-node');
for(const e of scienceEdges)assert.equal((await saved()).topology.edges.find(savedEdge=>savedEdge.id===e.id).origin,'custom');
click('',{topologyAction:'archetype',value:original.config.archetype}); await settled();
click('',{topologyAction:'archetype',value:'spine'}); await settled();
const renamed=await saved(); assert.ok(renamed.topology.nodes.includes('Research'));
for(const e of scienceEdges)assert.ok(renamed.topology.edges.some(savedEdge=>savedEdge.id===e.id&&savedEdge.origin==='custom'&&(savedEdge.from==='Research'||savedEdge.to==='Research')));
for(let i=0;i<3;i++)click('undo');
assert.ok((await saved()).topology.nodes.includes('Science'));

// Identity SVG transforms verify pointer event wiring, movement and JSON persistence.
// Actual browser hit testing and screen-to-map transforms remain outside this test.
dragNode('Medical',5,3);
const firstPosition=(await saved()).topology.nodePositions.Medical;
assert.ok(Number.isInteger(firstPosition.x)&&Number.isInteger(firstPosition.y));
dragNode('Medical',4,2);
const moved=await saved();
assert.equal(moved.topology.nodePositions.Medical.x,firstPosition.x+4);
assert.equal(moved.topology.nodePositions.Medical.y,firstPosition.y+2);
const imported=await (await nativeFetch(`${base}/api/import`,{
    method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(moved)
})).json();
assert.equal(JSON.stringify(imported.topology.nodePositions.Medical),JSON.stringify(moved.topology.nodePositions.Medical));
click('undo'); assert.equal(JSON.stringify((await saved()).topology.nodePositions.Medical),JSON.stringify(firstPosition)); click('redo');

// Defaults affect new dependencies while individual methods remain editable.
change({},'','default-shortcuts','checkbox',true);
change({},'','default-mail','checkbox',true);

// Patient access removes incompatible defaults and disables their method controls.
action('new-edge'); field('from','Medical'); field('to','Commons'); field('purpose','patients');
field('description','Patient access requires suitable walking passages.');
const patientMethods=el('topology-details').innerHTML;
for(const name of ['disposalShortcut','mail']) {
    const input=patientMethods.match(new RegExp(`<input\\b[^>]*id="edge-mode-${name}"[^>]*>`))?.[0];
    assert.ok(input&&input.includes('disabled'),`${name} must be disabled for patients`);
    assert.ok(!input.includes('checked'),`${name} must be removed when the purpose changes`);
}
action('save-edge');
const patientEdge=(await saved()).topology.edges.find(e=>e.description==='Patient access requires suitable walking passages.');
assert.ok(patientEdge); assert.equal(patientEdge.allowedModes.join(','),'publicHall');
click('',{deleteDependency:patientEdge.id});

// Connect nodes creates an editable draft with endpoints selected by two clicks.
action('connect'); click('',{topologyNode:'Science'}); click('',{topologyNode:'Arrivals'});
field('description','Two-click connection draft.'); action('save-edge');
const connected=(await saved()).topology.edges.find(e=>e.description==='Two-click connection draft.');
assert.ok(connected); assert.equal(connected.from,'Science'); assert.equal(connected.to,'Arrivals');
click('',{deleteDependency:connected.id});

action('new-edge');
field('from','Science'); field('to','Service'); field('relation','delivery'); field('purpose','items');
field('direction','forward'); field('weight',3.5,'number');
field('description','Deliver <specimens> & supplies through the service network.');
action('save-edge');
let p=await saved();
const edge=p.topology.edges.find(e=>e.description.startsWith('Deliver <specimens>'));
assert.ok(edge,'Persist dependency with its description');
assert.equal(p.topology.edges.length,original.topology.edges.length+1);
assert.equal(edge.priority,'desired'); assert.equal(edge.weight,3.5); assert.equal(edge.direction,'forward');
assert.ok(edge.allowedModes.includes('disposalShortcut')); assert.ok(edge.allowedModes.includes('mail'));
assert.match(el('inspector-content').innerHTML,/&lt;specimens&gt; &amp; supplies/);

// Edit the selected edge, retain its identity, remove it, and undo removal.
click('',{topologyEdge:edge.id}); field('weight',0,'number'); action('save-edge');
assert.equal((await saved()).topology.edges.find(e=>e.id===edge.id).weight,3.5);
assert.match(el('dependency-error').textContent,/Weight/);
click('',{dependencyPriority:'avoided'}); click('',{dependencyWeight:'8'});
field('description','Prefer a separate service route for specimens.'); field('maxDistance',25,'number');
action('save-edge'); p=await saved();
assert.equal(p.topology.edges.length,original.topology.edges.length+1);
assert.equal(p.topology.edges.find(e=>e.id===edge.id).priority,'avoided');
assert.equal(p.topology.edges.find(e=>e.id===edge.id).weight,8); assert.match(el('canvas').innerHTML,/Avoided/);
click('',{deleteDependency:edge.id}); assert.ok(!(await saved()).topology.edges.some(e=>e.id===edge.id));
click('undo'); assert.ok((await saved()).topology.edges.some(e=>e.id===edge.id));
click('',{topologyEdge:edge.id}); click('',{dependencyPriority:'desired'});
field('maxDistance','','number'); action('save-edge');

// Selecting another structural preset must preserve authored dependencies.
click('',{topologyAction:'archetype',value:'hub'}); await settled();
p=await saved(); assert.equal(p.config.archetype,'hub');
assert.ok(p.topology.edges.some(e=>e.id===edge.id));

// Department add, description, rename, links, removal and undo/redo work together.
action('add-node'); el('topology-node-name').value='Transit services';
el('topology-node-description').value='Coordinates passenger and delivery routes.'; action('save-node');
action('node-link'); field('to','Medical'); field('description','Bring transit services within reach of Medical.');
action('save-edge'); p=await saved();
assert.ok(p.topology.nodes.includes('Transit services'));
assert.ok(p.topology.edges.some(e=>e.from==='Transit services'&&e.to==='Medical'));
click('',{topologyNode:'Transit services'}); action('remove-node'); p=await saved();
assert.ok(!p.topology.nodes.includes('Transit services'));
assert.ok(!p.topology.edges.some(e=>e.from==='Transit services'||e.to==='Transit services'));
click('undo'); assert.ok((await saved()).topology.edges.some(e=>e.from==='Transit services')); click('redo');

// A low weight never makes Required optional. A mail-only dependency can be a
// valid concept, but the legacy geometry preview must not claim to implement it.
click('',{topologyEdge:edge.id}); click('',{dependencyPriority:'required'});
field('weight',.1,'number'); mode('publicHall',false); mode('disposalShortcut',false); action('save-edge');
click('run-stage'); await settled();
assert.equal(el('result-badge').textContent,'CONCEPT CHECKED',el('diagnostics').innerHTML);
click('generate'); await settled();
assert.equal(el('result-badge').textContent,'NEEDS WORK');
assert.match(el('diagnostics').innerHTML,/unsupported-fulfillment/);
assert.equal(requests.filter(r=>r.url==='/api/spatial/generate').at(-1).body.topology.edges.find(e=>e.id===edge.id).weight,.1);
click('undo'); click('',{stage:'0'}); click('',{topologyEdge:edge.id});
click('',{dependencyPriority:'desired'}); mode('publicHall',true); action('save-edge');
click('run-stage'); await settled();
assert.equal(el('result-badge').textContent,'CONCEPT CHECKED',el('diagnostics').innerHTML);

// Later stages and full generation use the authored topology.
click('topology-next'); await settled(); assert.equal(el('stage-title').textContent,'Placement');
click('run-stage'); await settled(); assert.equal(el('stage-title').textContent,'Department growth');
click('run-stage'); await settled(); assert.equal(el('stage-title').textContent,'Maintenance');
click('run-stage'); await settled();
assert.equal(el('result-badge').textContent,'GEOMETRY CHECKED',el('diagnostics').innerHTML); assert.match(el('metrics').innerHTML,/SHA256/);
click('generate'); await settled(); assert.equal(el('result-badge').textContent,'GEOMETRY CHECKED',el('diagnostics').innerHTML);
p=await saved();
assert.ok(p.topology.edges.some(e=>e.id===edge.id&&e.description==='Prefer a separate service route for specimens.'));
// The program tab uses the same project/history and invalidates nested geometry.
click('',{stage:'0'});click('',{topologyPanel:'program'});
assert.match(el('inspector-content').innerHTML,/STATION PROGRAM/);
click('',{programSelect:'Engineering/Atmos/TEG'});
assert.match(el('topology-details').innerHTML,/Required access origins/);
const beforeProgram=await saved();
change({programField:'description'},'Shared engineering and atmosphere generator.');
p=await saved();assert.equal(p.config.departments.find(d=>d.id==='Engineering').subdepartments[0].rooms[0].description,'Shared engineering and atmosphere generator.');
assert.equal(p.spatialPlan.regions.length,0);assert.equal(p.spatialPlan.hierarchy.roomFeatures.length,0);
click('undo');assert.equal(JSON.stringify((await saved()).spatialPlan.hierarchy),JSON.stringify(beforeProgram.spatialPlan.hierarchy));
console.log('Frontend topology passed: captured node selection/cancel, remembered template removals, renamed dependency preservation, node dragging/persistence, two-click connection, patient method compatibility, priorities, descriptions, weights, dependency CRUD/undo, department lifecycle, concept checks, unsupported fulfillment and later stages.');
