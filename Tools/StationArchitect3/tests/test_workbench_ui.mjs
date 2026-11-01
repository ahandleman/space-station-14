// Pure DOM/API simulation: no browser or generation engine is needed.
// import('./test_workbench_ui.mjs').then(m => m.runUITests())
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';

class Element {
  constructor(tagName = 'div') {
    this.tagName = tagName.toUpperCase();
    this.children = [];
    this.dataset = {};
    this.style = {};
    this.attributes = {};
    this.value = '';
    this.hidden = false;
    this.disabled = false;
    this.contentDocument = null;
    this.ownText = '';
    this.className = '';
    this.classes = new Set();
    this.classList = {
      add: (...names) => names.forEach(n => this.classes.add(n)),
      remove: (...names) => names.forEach(n => this.classes.delete(n)),
      toggle: (name, value) => value ? this.classes.add(name) : this.classes.delete(name)
    };
  }
  set textContent(value) { this.ownText = String(value); this.children = []; }
  get textContent() { return this.ownText + this.children.map(c => c.textContent).join(''); }
  append(...nodes) {
    for (const node of nodes) {
      if (this.tagName === 'SELECT' && !this.children.length) this.value = node.value;
      this.children.push(node);
    }
  }
  replaceChildren(...nodes) {
    this.ownText = '';
    this.children = [];
    if (this.tagName === 'SELECT') this.value = '';
    this.append(...nodes);
  }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name] ?? null; }
  removeAttribute(name) { delete this.attributes[name]; }
  set src(value) { this.attributes.src = value; }
  get src() { return this.attributes.src; }
  reportValidity() { return true; }
}

function scope(id, name, kind, area, owner) {
  return {id, name, kind, owner, organizationMode: owner === 'commons' ? 'distributed' : 'mixed',
    wholeOwner: kind === 'department', bounds: {min: 2, preferred: area, max: area*2},
    selectedRooms: 1, selectedFeatures: 1, abstractServices: 1, externalConnections: 2,
    rooms: [{id: id+'/room', name: name+' floor', kind: 'room'}]};
}

function report(unit, area, passed, quality = 'needs_review') {
  return {scope: unit, verification: {
    metrics: {department_floor: area, department_width: area, department_height: 1, department_aspect: area},
    inventory: {selectedRooms: 1, realizedRooms: 1, selectedFeatures: 1, realizedFeatures: 1, abstractServices: 1},
    hard_failures: passed ? [] : [{type:'metric', message:'Requested area cannot fit the selected equipment.'}],
    pendingRequirements: [
      {type:'metric', facet:'owner_organization', message:'Group-wide area awaits station integration.'},
      {type:'service_link', facet:'native_operation', message:'Power operation has not been evaluated.'}],
    externalContracts: [{type:'route', message:'Join the public hall during station integration.'}],
    layoutQuality: {status: quality, score: 2.5, checks:[
      {metric:'core.aspect_ratio', status:'needs_review', measured:2.713777, max:2.4},
      {metric:'core.fill_ratio', status:'needs_review', measured:0.536111, min:0.55},
      {metric:'circulation.fraction', status:'satisfied', measured:0.182999, max:0.25},
      {metric:'rooms.mean_fill_ratio', status:'satisfied', measured:0.813333, min:0.7, max:1},
      {metric:'shape.aspect_ratio', violation:1, measured:area}]}
  }};
}

function proposal(id, unit, area, passed) {
  return {id, svgUrl:'/runs/'+id+'/layout.svg', reportUrl:'/runs/'+id+'/report.json',
    report:report(unit, area, passed), rooms:[{id:unit.id+'/room', name:unit.name+' floor', area,
      physicalFeatures:1, abstractServices:1, features:[
        {id:'chair',name:'Chair',prototype:'Chair',abstract:false,placed:true,layer:'portable'},
        {id:'power',name:'Power demand',abstract:true,placed:false}]}]};
}

export async function runUITests() {
  const source = await readFile(new URL('../web/departments.js',import.meta.url),'utf8');
  const html = await readFile(new URL('../web/departments.html',import.meta.url),'utf8');
  const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(match => [match[1],
    new Element(['department','test-scope'].includes(match[1]) ? 'select' : 'div')]));
  elements.get('seed').value = '1';
  elements.get('aspect').value = '1';
  const arrivals = scope('arrivals','Arrivals','department',200,'arrivals');
  const restroom = scope('commons/restrooms[0]','Restroom 1','room',8,'commons');
  const lounge = scope('commons/lounge[0]','Lounge','room',24,'commons');
  const core = scope('command/core','Command core','core',300,'command');
  const personnel = scope('command/personnel[0]','HoP personnel suite','suite',40,'command');
  const catalog = {departments:[
    {id:'arrivals',name:'Arrivals',organization:{mode:'clustered'},bounds:{preferred:9999},scopes:[arrivals]},
    {id:'commons',name:'Commons',organization:{mode:'distributed'},bounds:{preferred:9999},scopes:[restroom,lounge]},
    {id:'command',name:'Command',organization:{mode:'mixed'},bounds:{preferred:9999},scopes:[core,personnel]}]};
  const requests = [], responses = [];
  const context = vm.createContext({
    document:{body:new Element('body'),getElementById:id=>{
      assert(elements.has(id),'Script references a missing UI element: '+id);
      return elements.get(id);
    },createElement:tag=>new Element(tag)},
    crypto:{getRandomValues:array=>{array[0]=42;return array;}},
    fetch:async (url, options)=>{
      requests.push({url,options});
      const data = url === '/api/catalog' ? catalog : responses.shift();
      assert(data,'Missing fake API response for '+url);
      return {ok:true,json:async()=>data};
    }
  });
  new vm.Script(source,{filename:'departments.js'}).runInContext(context);
  await new Promise(resolve=>setImmediate(resolve));
  const element = id=>elements.get(id);
  const choose = (id,value)=>{element(id).value=value;element(id).onchange();};
  const run = code=>vm.runInContext(code,context);
  const results = [];

  assert.equal(element('test-scope').value,'arrivals');
  assert.equal(element('area').value,200,'Use local scope bounds rather than legacy owner bounds.');
  assert.equal(element('generate').disabled,false);
  results.push('clustered scope uses its own bounds');

  choose('department','commons');
  assert.equal(element('test-scope').value,'');
  assert.equal(element('generate').disabled,true);
  assert.equal(element('resize').disabled,true);
  assert.equal(element('area').disabled,true);
  assert.equal(element('rooms').children.length,0);
  assert(!element('test-scope').children.some(n=>n.value==='commons'),'Distributed owner must not become a packing target.');
  const before = requests.length;
  await run('generate(false)');
  assert.equal(requests.length,before,'A distributed collection cannot submit without a selected local unit.');
  results.push('distributed owner cannot submit a whole-owner layout');

  choose('test-scope',restroom.id);
  assert.equal(element('area').value,8);
  assert.equal(element('generate').disabled,false);
  assert.equal(element('rooms').children.length,1);
  const accepted = proposal('accepted',restroom,8,true);
  responses.push({accepted:true,canResize:true,retainedPrevious:false,proposal:accepted,visible:accepted});
  await run('generate(false)');
  assert.deepEqual(JSON.parse(requests.at(-1).options.body),
    {department:'commons',scope:restroom.id,seed:1,area:8,aspect:1});
  assert.equal(element('map').src,accepted.svgUrl);
  assert.match(element('notice').textContent,/passed its local constraints/);
  assert.match(element('notice').textContent,/station placement is still pending/);
  assert.match(element('quality').textContent,/Needs review/,'Advisory layout failure must remain visible alongside a constraint pass.');
  assert.match(element('quality').textContent,/Needs review: Core stretch · measured 2\.71 · target max 2\.4/);
  assert.match(element('quality').textContent,/Core floor coverage · measured 53\.6% · target min 55%/);
  assert.match(element('quality').textContent,/Within target: Floor used by corridors · measured 18\.3% · target max 25%/);
  assert.match(element('quality').textContent,/Average room floor coverage · measured 81\.3% · target min 70%, max 100%/);
  assert.match(element('quality').textContent,/shape\.aspect_ratio · measured 8/,'Unrecognized metric names remain usable for future extensions.');
  assert.doesNotMatch(element('quality').textContent,/core\.aspect_ratio|core\.fill_ratio|circulation\.fraction|rooms\.mean_fill_ratio|needs_review/);
  assert.equal(Number(element('parent-count').textContent),1);
  assert.equal(Number(element('pending-count').textContent),1);
  assert.match(element('current-size').textContent,/8 × 1/);
  run(`selectRoom(${JSON.stringify(restroom.id+'/room')})`);
  assert.match(element('features').textContent,/No physical entity/);
  assert.doesNotMatch(element('features').textContent,/Not placed/);
  assert.doesNotMatch(element('inventory-stats').textContent,/objects placed/,'Older reports without part counts remain supported.');
  assert.doesNotMatch(element('features').textContent,/placed objects/,'Older feature records need no part listing.');
  results.push('scope payload, quality advice and parent obligations are distinct');

  accepted.report.verification.inventory.selectedNativeParts = 3;
  accepted.report.verification.inventory.realizedNativeParts = 3;
  const furnishing = accepted.rooms[0].features[0];
  furnishing.name = 'Table and chairs';
  furnishing.assembly = 'conversation_table';
  delete furnishing.prototype;
  furnishing.nativeParts = [
    {id:'table',prototype:'TableWood'},
    {id:'left_chair',prototype:'ChairWood'},
    {id:'right_chair',prototype:'ChairWood'}];
  responses.push({accepted:true,canResize:true,retainedPrevious:false,proposal:accepted,visible:accepted});
  await run('generate(false)');
  run(`selectRoom(${JSON.stringify(restroom.id+'/room')})`);
  assert.match(element('inventory-stats').textContent,/3\/3objects placed/);
  assert.match(element('features').textContent,/3 placed objects/);
  assert.match(element('features').textContent,/TableWood/);
  assert.equal((element('features').textContent.match(/ChairWood/g) || []).length,2);
  results.push('compound furnishings expose their placed parts without changing feature counts');

  element('area').value = 2;
  const rejected = proposal('rejected',restroom,2,false);
  responses.push({accepted:false,canResize:true,retainedPrevious:true,proposal:rejected,visible:accepted});
  await run('generate(true)');
  assert.equal(requests.at(-1).url,'/api/resize');
  assert.equal(JSON.parse(requests.at(-1).options.body).scope,restroom.id);
  assert.equal(element('map').src,accepted.svgUrl);
  assert.match(element('notice').textContent,/previous passing layout is still shown/);
  assert.match(element('checks-title').textContent,/Rejected change/);
  assert.match(element('quality').textContent,/Rejected proposal/);
  assert.equal(element('proposal-link').href,rejected.reportUrl);
  results.push('failed scoped resize retains the accepted preview');

  choose('test-scope',lounge.id);
  assert.equal(element('resize').disabled,true);
  assert.equal(element('map').hidden,true);
  assert.equal(element('area').value,24);
  choose('department','command');
  assert.equal(element('test-scope').value,core.id);
  assert.equal(element('area').value,300);
  choose('test-scope',personnel.id);
  assert.equal(element('area').value,40);
  assert.match(element('scope-description').textContent,/Local suite/);
  assert.match(element('organization').textContent,/independently placed/);
  results.push('changing local scope resets acceptance and mixed cores stay separate');
  return {passed:results.length,checks:results};
}
