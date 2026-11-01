// Actual localhost API and real controller handlers; browser rendering is a separate check.
import assert from 'node:assert/strict';
import {inflateRawSync} from 'node:zlib';
import {readFileSync} from 'node:fs';
import {flattenTree,findTreeNode,definitionAt,designSvg,treeHtml} from '../wwwroot/design-renderer.js';
import {newGameflowRule,gameflowWitnessSvg} from '../wwwroot/gameflow-panel.js';
import {systemsEditorHtml,nativeSystemsSvg} from '../wwwroot/systems-panel.js';
const base=globalThis.STATION_ARCHITECT_TEST_BASE||'http://127.0.0.1:8421',nativeFetch=globalThis.fetch,clone=value=>JSON.parse(JSON.stringify(value));
async function request(path,body){const response=await nativeFetch(base+'/api/'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});const result=response.headers.get('content-type')?.includes('json')?await response.json():await response.text();assert.equal(response.status,200,JSON.stringify(result));return result;}
const catalog=await request('default');
const baseRoom=clone(catalog.departments[0].rooms[0]);
function room(id,role){return {...clone(baseRoom),id,name:id,description:'Functional editor test room.',sourceRefs:[],role,count:{min:1,target:1,max:1,weight:1},area:{min:role==='filler'?9:12,target:role==='filler'?18:30,max:80,weight:1},shape:{maxAspectRatio:4,minFillRatio:.35,weight:1},connections:[],contents:[],exterior:{minSpaceFrontage:0,allowSpaceExposure:true},access:{level:'public',allowPublicEntry:role==='filler',allowMaintenanceEntry:role==='filler',entryOwners:[],requireNetworkApproach:[]},environment:{pressure:'standard',placement:'interior',sealed:true,minTemperatureKelvin:null,maxTemperatureKelvin:null},allowThroughTraffic:role==='filler'};}
const definition={schemaVersion:2,id:'EditorFixture',name:'Editable fixture',description:'Two owners with required crew routing.',sourceRefs:[],settings:{roomPressureWeight:1,departmentPressureWeight:1,connectionPressureWeight:1},choices:[],departments:['Alpha','Beta'].map((id,index)=>({id,name:id,description:'An owner composed of its rooms.',sourceRefs:[],color:index?'#b894d5':'#79b8cf',tags:[],area:{min:21,target:48,max:200,weight:1},shape:{maxAspectRatio:4,minFillRatio:.25,weight:1},split:{allow:false,maxComponents:1,componentPenalty:1000,hallCrossingPenalty:2000,maintenanceCrossingPenalty:1500},rooms:[room('Work','activity'),room('Circulation','filler')]})),connections:[{id:'PublicAccess',from:'Alpha/Work',to:['Beta/Work'],priority:'required',selection:'all',presence:'requireTarget',weight:5,modes:['publicHall'],kind:'walking',interfaceTypes:[],minWidth:1,maxDistance:null,description:'Reach the other owner through public circulation.'}],maintenance:{area:{min:0,target:0,max:200,weight:1},passageWidth:{min:2,max:3},rooms:[]}};
for(const owner of definition.departments)owner.rooms[0].connections=[{id:'InternalAccess',from:'',to:['Circulation'],priority:'required',selection:'all',presence:'requireTarget',weight:3,modes:['department'],kind:'walking',interfaceTypes:[],minWidth:1,maxDistance:null,description:'Owned circulation remains reachable.'}];
let project=await request('design/new',{definition});
assert.equal(project.tree.id,'$station');assert.equal(project.tree.children.length,3);
assert.equal(findTreeNode(project.tree,'Alpha/Work#1').parentId,'Alpha/Work');
for(const stage of ['selection','placement','growth','circulation','gameflow','contents','validation'])project=await request('design/stage/'+stage,project);
assert.equal(project.report.success,true,JSON.stringify(project.report.diagnostics));
assert.ok(project.layout.publicCorridors.length>0);assert.ok(project.layout.maintenance.length>0);
assert.ok(project.layout.rooms.every(item=>item.cells.length>0));
assert.equal(project.tree.floorArea,project.layout.rooms.reduce((sum,item)=>sum+item.cells.length,0));
const flowDefinition=clone(project.definition),flowRule=newGameflowRule('WorkTravel');
Object.assign(flowRule,{kind:'travelDistance',bounds:{min:0,target:null,max:1000},from:{paths:['Alpha/Work'],tags:[],owners:[],roles:[],networks:[],match:'any'},to:{paths:['Beta/Work'],tags:[],owners:[],roles:[],networks:[],match:'any'}});
flowDefinition.gameflow.rules=[flowRule];
let flowProject=await request('design/definition',{project,definition:flowDefinition});
assert.deepEqual(flowProject.layout.rooms.map(room=>room.cells),project.layout.rooms.map(room=>room.cells));
assert.equal(flowProject.stageStates.find(item=>item.stage==='placement').status,'ready');
flowProject=await request('design/stage/gameflow',flowProject);
assert.equal(flowProject.gameflow.rules[0].observed,true,JSON.stringify(flowProject.gameflow));
assert.equal(flowProject.gameflow.rules[0].satisfied,true);
assert.match(gameflowWitnessSvg(flowProject.gameflow,'WorkTravel'),/gameflow-witnesses/);
const trueDistance=flowProject.gameflow.rules[0].value;flowProject.gameflow.rules[0].value=999999;
const refreshedFlow=await request('design/import',flowProject);
assert.equal(refreshedFlow.gameflow.rules[0].value,trueDistance,'Saved gameflow observations must be recomputed.');
const floor=project.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells;
const originalDefinition=JSON.stringify(project.definition);
const pinned=await request('design/edit',{project,edit:{kind:'pin-room',nodeId:'Alpha/Work#1',enabled:true}});
assert.equal(pinned.layout.rooms.find(item=>item.id==='Alpha/Work#1').pinned,true);
const locked=await request('design/edit',{project:pinned,edit:{kind:'lock-room',nodeId:'Alpha/Work#1',enabled:true}});
const grown=await request('design/stage/growth',locked);
assert.deepEqual(grown.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells,floor,'Rerunning growth must preserve a locked mask.');
assert.equal(JSON.stringify(grown.definition),originalDefinition);
const revised=clone(grown.definition),work=definitionAt(revised,'Alpha/Work');work.area.min=floor.length+1;work.area.target=floor.length+1;work.area.max=Math.max(work.area.max,floor.length+20);
const conflict=await request('design/definition',{project:grown,definition:revised});
assert.deepEqual(conflict.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells,floor);
assert.equal(conflict.report.success,false,'A changed hard minimum must stay active against locked geometry.');
assert.equal(findTreeNode(conflict.tree,'Alpha/Work#1').requirements.area.min,floor.length+1);
assert.ok(conflict.stageStates.some(item=>['dirty','stale'].includes(item.status)));
const restored=await request('design/import',project);assert.equal(restored.report.hash,project.report.hash);
const svg=await request('design/export/svg',project);assert.match(svg,/<svg/);assert.match(svg,/Alpha\/Work#1/);
assert.match(designSvg(project,{stage:'growth'}),/data-room="Alpha\/Work#1"/);
assert.match(treeHtml(project,'Alpha/Work#1',new Set(['$station','Alpha','Alpha/Work'])),/Alpha\/Work#1/);
const map=await request('design/export/map',project);assert.match(map,/format: 7/);assert.match(map,/MapGrid/);
const manifest=await request('design/export/manifest',project);assert.equal(manifest.simulationVerified,false);assert.equal(manifest.operational,false);
const zipped=await nativeFetch(base+'/api/design/export/bundle',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(project)});assert.equal(zipped.status,200);const bytes=Buffer.from(await zipped.arrayBuffer()),entries=new Map();let offset=0;
while(offset+30<=bytes.length&&bytes.readUInt32LE(offset)===0x04034b50){const method=bytes.readUInt16LE(offset+8),size=bytes.readUInt32LE(offset+18),nameLength=bytes.readUInt16LE(offset+26),extraLength=bytes.readUInt16LE(offset+28),name=bytes.subarray(offset+30,offset+30+nameLength).toString(),start=offset+30+nameLength+extraLength,content=bytes.subarray(start,start+size);entries.set(name,(method===8?inflateRawSync(content):content).toString());offset=start+size;}
assert.ok([...entries.keys()].some(name=>name.startsWith('Resources/Maps/Generated/')));assert.ok(entries.has('project.station.json'));assert.ok(entries.has('systems-manifest.json'));
assert.equal(JSON.parse(entries.get('project.station.json')).definition.id,'EditorFixture');

const nativeProfiles=JSON.parse(readFileSync(new URL('../Catalog/native-profiles.json',import.meta.url),'utf8')).profiles;
const systemDefinition=clone(project.definition);
const service=(id,profileId,prototype)=>({id,profileId,scope:'room',selector:{networks:['rooms']},content:{id,prototypes:[prototype],count:{min:1,target:1,max:1},placement:'boundary'}});
systemDefinition.systems={enabled:true,profiles:nativeProfiles.filter(profile=>['apc','light','lv-cable'].includes(profile.id)),
    devices:[service('Power','apc','APCBasic'),service('Lighting','light','Poweredlight')],
    networks:[{id:'LightingWire',kind:'power',scope:'compartment',segmentProfiles:['lv-cable'],terminals:[{deviceRuleId:'Power',portId:'output',role:'source'},{deviceRuleId:'Lighting',portId:'power',role:'consumer'}]}],controls:[]};
let nativeProject=await request('design/definition',{project,definition:systemDefinition});
assert.deepEqual(nativeProject.layout.rooms.map(room=>room.cells),project.layout.rooms.map(room=>room.cells));
for(const stage of ['systemsDevices','power','atmos','disposals','validation'])nativeProject=await request('design/stage/'+stage,nativeProject);
assert.equal(nativeProject.report.success,true,JSON.stringify(nativeProject.report.diagnostics));
assert.ok(nativeProject.layout.systemDevices.length>=8);
assert.ok(nativeProject.systems.terminals.length>0);
assert.ok(nativeProject.systems.terminals.every(terminal=>terminal.sourceReachable));
assert.match(nativeSystemsSvg(nativeProject,true),/data-system-segment=/);
assert.match(systemsEditorHtml(nativeProject.definition.systems),/Bind crew-tube and mail endpoints/);
const nativeSegment=nativeProject.layout.systemNetworks.flatMap(network=>network.segments)[0];
const nativeMoved=await request('design/edit',{project:nativeProject,edit:{kind:'move-system-segment',nodeId:nativeSegment.id,position:{x:nativeSegment.position.x+12,y:nativeSegment.position.y+12}}});
assert.ok(nativeMoved.layout.systemNetworks.some(network=>network.locked&&network.segments.some(segment=>segment.id===nativeSegment.id&&segment.edited)));
const nativeMap=await request('design/export/map',nativeProject);assert.match(nativeMap,/CableApcExtension/);assert.match(nativeMap,/APCBasic/);
const nativeManifest=await request('design/export/manifest',nativeProject);assert.equal(nativeManifest.simulationVerified,false);assert.equal(nativeManifest.operational,false);

const elements=new Map(),listeners=new Map(),storage=new Map();let settingInputs=[];
function element(id){if(!elements.has(id))elements.set(id,{id,innerHTML:'',textContent:'',value:'',checked:true,hidden:false,dataset:{},listeners:new Map(),attributes:{},addEventListener(type,fn){this.listeners.set(type,fn);},setAttribute(key,value){this.attributes[key]=value;},click(){return this.listeners.get('click')?.({target:this});},matches(){return false;},setPointerCapture(){},createSVGPoint(){return{x:0,y:0,matrixTransform(){return{x:this.x,y:this.y};}};},getScreenCTM(){return{inverse(){return{};}};}});return elements.get(id);}
globalThis.document={getElementById:element,addEventListener(type,fn){listeners.set(type,fn);},querySelectorAll(selector){return selector==='[data-setting]'?settingInputs:[];},querySelector(){return element('source-details');},createElement(){return{click(){}};}};
storage.set('station-architect-design-v1',JSON.stringify({project,stage:'growth',selected:'Alpha/Work#1'}));
globalThis.localStorage={getItem:key=>storage.get(key)||null,setItem:(key,value)=>storage.set(key,value),removeItem:key=>storage.delete(key)};
let lastProject;
globalThis.fetch=async(url,options)=>{const response=await nativeFetch(new URL(url,base),options);if(response.ok&&String(url).startsWith('/api/design/')&&response.headers.get('content-type')?.includes('json')){const value=await response.clone().json();if(value.definition&&value.layout)lastProject=value;}return response;};
const waitFor=async(condition)=>{for(let index=0;index<400;index++){if(condition())return;await new Promise(resolve=>setTimeout(resolve,25));}throw new Error('Designer UI operation timed out: '+element('design-status').textContent);};
const click=dataset=>listeners.get('click')({target:{closest(){return{dataset};}}});
await import('../wwwroot/designer.js');await waitFor(()=>element('design-status').textContent.startsWith('Ready.'));
assert.match(element('design-tree').innerHTML,/Alpha\/Work#1/);assert.match(element('design-node-inspector').innerHTML,/Requirements above this node/);assert.match(element('design-canvas').innerHTML,/data-room=/);
click({edit:'lock-room',enabled:'true'});await waitFor(()=>lastProject?.layout.rooms.find(item=>item.id==='Alpha/Work#1').floorLocked===true);
const beforeFloor=lastProject.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells.length;
click({tool:'erase-room'});const cell=floor[0],event={pointerId:1,clientX:cell.x+.5,clientY:cell.y+.5,button:0,shiftKey:false,target:{closest(){return null;}}};element('design-canvas').listeners.get('pointerdown')(event);await element('design-canvas').listeners.get('pointerup')(event);
await waitFor(()=>lastProject?.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells.length===beforeFloor-1);
assert.equal(lastProject.tree.floorArea,project.tree.floorArea-1);
assert.equal(JSON.stringify(lastProject.definition),originalDefinition);
await element('design-undo').click();await waitFor(()=>lastProject?.layout.rooms.find(item=>item.id==='Alpha/Work#1').cells.length===beforeFloor);
click({node:'Alpha/Work'});assert.match(element('design-node-inspector').innerHTML,/Selected instances/);
element('template-count').value='0';click({action:'set-template-count'});await waitFor(()=>lastProject?.selection.roomCounts['Alpha/Work']===0);
assert.equal(lastProject.definition.departments[0].rooms[0].count.min,1);
assert.equal(lastProject.report.success,false);assert.ok(findTreeNode(lastProject.tree,'Alpha/Work#1'),'An invalid mandatory count override must retain the original instance for correction.');
const optionalDefinition=clone(lastProject.definition);definitionAt(optionalDefinition,'Alpha/Work').count.min=0;
element('design-yaml').value=await request('yaml/export',optionalDefinition);
await element('apply-design-yaml').click();await waitFor(()=>flattenTree(lastProject.tree).some(node=>node.orphaned));
assert.match(element('design-tree').innerHTML,/Preserved stale geometry/);

// Exercise the new controls through the actual designer handlers and HTTP contract.
settingInputs=[{dataset:{setting:'packing.compactness'},type:'number',value:'.65'},
    {dataset:{setting:'packing.departmentBundling'},type:'number',value:'.7'},
    {dataset:{setting:'packing.maintenanceCoverage'},type:'number',value:'.6'},
    {dataset:{setting:'packing.internalVariation'},type:'number',value:'.7'},
    {dataset:{setting:'packing.silhouetteVariation'},type:'number',value:'.55'},
    {dataset:{setting:'packing.interlock'},type:'number',value:'.8'}];
await element('apply-design-settings').click();await waitFor(()=>lastProject?.settings.packing.compactness===.65);
assert.equal(lastProject.settings.packing.departmentBundling,.7);assert.equal(lastProject.settings.packing.maintenanceCoverage,.6);
assert.match(element('design-settings-fields').innerHTML,/packing\.maintenanceConnectionsPerCluster/);
assert.equal(lastProject.settings.packing.internalVariation,.7);assert.equal(lastProject.settings.packing.silhouetteVariation,.55);assert.equal(lastProject.settings.packing.interlock,.8);
const nestedDefinition=clone(definition),parent=definitionAt(nestedDefinition,'Alpha/Work');
parent.count={min:1,target:2,max:2,weight:1};parent.rooms=[room('Booth','activity')];
parent.rooms[0].count={min:0,target:1,max:2,weight:1};
const nested=await request('design/new',{definition:nestedDefinition});
await element('design-file').listeners.get('change')({target:{files:[{size:1,text:async()=>JSON.stringify({project:nested,stage:'selection',selected:'Alpha/Work#1/Booth'})}],value:'nested.json'}});
assert.match(element('design-node-inspector').innerHTML,/Instances in this parent/);
element('template-count').value='2';click({action:'set-template-count'});await waitFor(()=>lastProject?.selection.parentRoomCounts['Alpha/Work#1/Booth']===2);
assert.equal(lastProject.layout.rooms.filter(item=>item.parentInstanceId==='Alpha/Work#1').length,2);
assert.equal(lastProject.layout.rooms.filter(item=>item.parentInstanceId==='Alpha/Work#2').length,1);
await listeners.get('change')({target:{dataset:{requirement:'nesting.allowExternalEntry'},type:'checkbox',checked:true,checkValidity:()=>true}});
assert.equal(definitionAt(lastProject.definition,'Alpha/Work/Booth').nesting.allowExternalEntry,true);
click({node:'Alpha/Work'});
await listeners.get('change')({target:{dataset:{requirement:'placement.policy'},type:'select-one',value:'distributed',checkValidity:()=>true}});
assert.equal(definitionAt(lastProject.definition,'Alpha/Work').placement.policy,'distributed');
const dockProject=await request('design/default');
await element('design-file').listeners.get('change')({target:{files:[{size:1,text:async()=>JSON.stringify({project:dockProject,stage:'selection',selected:'Arrivals/Waiting'})}],value:'docking.json'}});
assert.match(element('design-node-inspector').innerHTML,/Shuttle clearance requirements/);
await listeners.get('change')({target:{dataset:{requirement:'exterior.docks.0.berthWidth'},type:'number',value:'23',checkValidity:()=>true}});
await listeners.get('change')({target:{dataset:{requirement:'exterior.docks.0.directions'},type:'select-multiple',multiple:true,selectedOptions:[{value:'east'},{value:'south'}],checkValidity:()=>true}});
const editedDock=definitionAt(lastProject.definition,'Arrivals/Waiting').exterior.docks[0];
assert.equal(editedDock.berthWidth,23);assert.equal(JSON.stringify(editedDock.directions),JSON.stringify(['east','south']));
assert.equal(editedDock.contentId,'ArrivalDockAirlocks');
assert.deepEqual(definitionAt(lastProject.definition,'Arrivals/Waiting').area,definitionAt(dockProject.definition,'Arrivals/Waiting').area);
await click({gameflowAction:'add-rule'});
const addedRule=lastProject.definition.gameflow.rules.at(-1),addedIndex=lastProject.definition.gameflow.rules.length-1;
assert.ok(addedRule.id);assert.match(element('gameflow-editor').innerHTML,/Pressure weight/);
await listeners.get('change')({target:{dataset:{gameflowPath:'rules.'+addedIndex+'.weight'},type:'number',value:'7.5',checkValidity:()=>true}});
assert.equal(lastProject.definition.gameflow.rules.at(-1).weight,7.5);
await listeners.get('change')({target:{dataset:{gameflowPath:'accessProfiles.0.id'},type:'text',value:'Crew',checkValidity:()=>true}});
assert.equal(lastProject.definition.gameflow.rules.at(-1).accessProfile,'Crew','Renaming an access profile must preserve rule bindings.');
await click({gameflowAction:'remove-rule',index:String(addedIndex)});
assert.ok(!lastProject.definition.gameflow.rules.some(rule=>rule.id===addedRule.id));
await element('design-file').listeners.get('change')({target:{files:[{size:1,text:async()=>JSON.stringify({project:nativeProject,stage:'power',selected:'Alpha/Work#1'})}],value:'native.json'}});
assert.match(element('systems-editor').innerHTML,/LightingWire/);assert.match(element('systems-report').innerHTML,/witnessed source path/);
await click({tool:'system-segment'});
const segmentEvent={pointerId:2,clientX:nativeSegment.position.x+.5,clientY:nativeSegment.position.y+.5,button:0,shiftKey:false,
    target:{closest:selector=>selector==='[data-system-segment]'?{dataset:{systemSegment:nativeSegment.id}}:null}};
element('design-canvas').listeners.get('pointerdown')(segmentEvent);
assert.match(element('design-node-inspector').innerHTML,/Rotate 90 degrees/);
await element('design-canvas').listeners.get('pointerup')({...segmentEvent,clientX:segmentEvent.clientX+1});
assert.ok(lastProject.layout.systemNetworks.some(network=>network.locked&&network.segments.some(segment=>segment.id===nativeSegment.id&&segment.position.x===nativeSegment.position.x+1)));
await click({nativeEdit:'rotate-system-segment',id:nativeSegment.id,value:'90'});
assert.equal(lastProject.layout.systemNetworks.flatMap(network=>network.segments).find(segment=>segment.id===nativeSegment.id).rotation,90);
const wireProfileIndex=lastProject.definition.systems.profiles.findIndex(profile=>profile.id==='lv-cable');
await listeners.get('change')({target:{dataset:{systemsPath:'profiles.'+wireProfileIndex+'.id'},type:'text',value:'renamed-wire',checkValidity:()=>true}});
assert.equal(lastProject.definition.systems.networks[0].segmentProfiles[0],'renamed-wire');
assert.equal(lastProject.stageStates.find(item=>item.stage==='placement').status,'ready');
await listeners.get('change')({target:{dataset:{systemsPath:'networks.0.maxLength'},type:'number',value:'128',checkValidity:()=>true}});
assert.equal(lastProject.definition.systems.networks[0].maxLength,128);
console.log('Designer frontend/API passed: staged generation, runtime tree, locks, edits, undo, packing, nesting, docks, gameflow rules, native service generation/export and infrastructure controls. Browser rendering not covered.');
