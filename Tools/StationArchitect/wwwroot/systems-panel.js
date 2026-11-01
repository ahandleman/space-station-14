import {escapeHtml as esc} from './design-renderer.js';

const field=(label,path,value,type='text')=>`<label>${esc(label)}<input data-systems-path="${esc(path)}" type="${type}" ${type==='number'?'step="any"':''} value="${esc(value??'')}"></label>`;
const select=(label,path,value,choices)=>`<label>${esc(label)}<select data-systems-path="${esc(path)}">${[...new Set([value,...choices])].map(choice=>`<option ${choice===value?'selected':''}>${esc(choice)}</option>`).join('')}</select></label>`;
const json=(label,path,value)=>`<label class="wide">${esc(label)}<textarea data-systems-path="${esc(path)}" data-systems-type="json" spellcheck="false">${esc(JSON.stringify(value??{},null,2))}</textarea></label>`;
const list=(label,path,value)=>`<label>${esc(label)}<input data-systems-path="${esc(path)}" data-systems-type="list" value="${esc((value||[]).join(', '))}"></label>`;
const check=(label,path,value)=>`<label class="check"><input data-systems-path="${esc(path)}" type="checkbox" ${value?'checked':''}>${esc(label)}</label>`;
const scopes=['room','compartment','cluster','owner','station'];
export function systemsEditorHtml(program){
    const s=program||{},profiles=s.profiles||[],devices=s.devices||[],networks=s.networks||[],controls=s.controls||[];
    let html='<p>Configure native hardware, its actual ports, named networks and controller links. These requirements are stored in the room-program YAML. Rebuild service devices after changing coverage or profiles.</p>'+check('Generate native station infrastructure','enabled',s.enabled);
    html+='<p class="node-small">Power, atmos and disposal connections are checked against the declared native profiles. Source storage, gas reserves and connectivity are planning evidence; runtime operation still requires loading and testing the map in SS14.</p><details><summary>Service device coverage</summary>';
    html+=devices.map((rule,index)=>{const p='devices.'+index;return `<details class="gameflow-card"><summary>${esc(rule.id)} / ${esc(rule.profileId)} / ${esc(rule.scope)}</summary><div class="fields">${field('ID',p+'.id',rule.id)}${field('Description',p+'.description',rule.description)}${select('Native profile',p+'.profileId',rule.profileId,profiles.filter(profile=>profile.kind==='device').map(profile=>profile.id))}${select('Coverage scope',p+'.scope',rule.scope,scopes)}${field('Role label',p+'.role',rule.role)}${check('Bind existing room contents',p+'.bindExisting',rule.bindExisting)}${check('Bind physical walking doors',p+'.bindInterfaces',rule.bindInterfaces)}${check('Bind crew-tube and mail endpoints',p+'.bindTransport',rule.bindTransport)}${list('Additional native profile variants',p+'.profileIds',rule.profileIds)}${list('Existing content IDs',p+'.contentIds',rule.contentIds)}${field('Maximum coverage distance',p+'.maxCoverageDistance',rule.maxCoverageDistance,'number')}${json('Room selector',p+'.selector',rule.selector)}${json('Content count, placement and working area',p+'.content',rule.content)}${list('Reference sources',p+'.sourceRefs',rule.sourceRefs)}</div><button data-systems-action="remove" data-collection="devices" data-index="${index}">Remove device rule</button></details>`;}).join('');
    html+='<button data-systems-action="add" data-collection="devices">Add service device rule</button></details><details><summary>Named power, gas and waste networks</summary>';
    html+=networks.map((rule,index)=>{const p='networks.'+index;return `<details class="gameflow-card"><summary>${esc(rule.id)} / ${esc(rule.kind)}</summary><div class="fields">${field('ID',p+'.id',rule.id)}${field('Description',p+'.description',rule.description)}${select('System kind',p+'.kind',rule.kind,['power','gas','disposal'])}${select('Scope',p+'.scope',rule.scope,scopes)}${select('Priority',p+'.priority',rule.priority,['required','desired'])}${field('Maximum routed length',p+'.maxLength',rule.maxLength,'number')}${field('Minimum initial gas pressure kPa',p+'.minPressureKpa',rule.minPressureKpa??0,'number')}${check('Allow crossing space',p+'.allowSpace',rule.allowSpace)}${list('Allowed segment profile IDs',p+'.segmentProfiles',rule.segmentProfiles)}${json('Device-rule terminal bindings and roles',p+'.terminals',rule.terminals)}</div><button data-systems-action="remove" data-collection="networks" data-index="${index}">Remove network</button></details>`;}).join('');
    html+='<button data-systems-action="add" data-collection="networks">Add network</button></details><details><summary>Control links</summary>';
    html+=controls.map((rule,index)=>{const p='controls.'+index;return `<details class="gameflow-card"><summary>${esc(rule.id)}</summary><div class="fields">${field('ID',p+'.id',rule.id)}${select('Controller device rule',p+'.controllerRuleId',rule.controllerRuleId,devices.map(device=>device.id))}${list('Controlled device rule IDs',p+'.deviceRuleIds',rule.deviceRuleIds)}${select('Scope',p+'.scope',rule.scope,scopes)}${field('Native control component',p+'.component',rule.component)}${field('Maximum linked devices',p+'.maxDevices',rule.maxDevices,'number')}${select('Priority',p+'.priority',rule.priority,['required','desired'])}</div><button data-systems-action="remove" data-collection="controls" data-index="${index}">Remove control rule</button></details>`;}).join('');
    html+='<button data-systems-action="add" data-collection="controls">Add control rule</button></details><details><summary>Native prototype and port profiles</summary><p class="node-small">Profiles describe real prototype ports, voltage groups, pipe layers, directions and transfer behavior. Source references should identify the native prototype/code evidence. Changing a profile does not change the underlying SS14 prototype.</p>';
    html+=profiles.map((profile,index)=>{const p='profiles.'+index;return `<details class="gameflow-card"><summary>${esc(profile.id)} / ${esc(profile.prototype)}</summary><div class="fields">${field('ID',p+'.id',profile.id)}${field('Native prototype',p+'.prototype',profile.prototype)}${select('Profile kind',p+'.kind',profile.kind,['device','cable','pipe','disposal'])}${check('Requires power','profiles.'+index+'.requiresPower',profile.requiresPower)}${field('Placement layer',p+'.occupancyLayer',profile.occupancyLayer||'auto')}${check('Blocks crew on floor',p+'.blocksCrew',profile.blocksCrew!==false)}${list('Required native components',p+'.requiredComponents',profile.requiredComponents)}${json('Named native ports',p+'.ports',profile.ports)}${json('Transfers between ports',p+'.transfers',profile.transfers)}${json('Initial power or gas sources',p+'.sources',profile.sources)}${json('Native component overrides',p+'.components',profile.components)}${list('Reference sources',p+'.sourceRefs',profile.sourceRefs)}</div><button data-systems-action="remove" data-collection="profiles" data-index="${index}">Remove profile</button></details>`;}).join('');
    return html+'<button data-systems-action="add" data-collection="profiles">Add native profile</button></details><details><summary>Generation and evidence budgets</summary>'+json('Generation budgets','generation',s.generation||{})+'</details>';
}
export function newSystemsItem(collection,id,program){
    if(collection==='profiles')return{id,prototype:'',kind:'device',requiredComponents:[],ports:[],transfers:[],sources:[],requiresPower:false,occupancyLayer:'auto',blocksCrew:true,components:{},sourceRefs:[]};
    if(collection==='devices'){const profile=(program.profiles||[]).find(item=>item.kind==='device');return{id,description:'',selector:{paths:[],tags:[],roles:[],pressures:['standard'],placements:['interior'],includeNested:true},scope:'room',profileId:profile?.id||'',role:'',content:{id,prototypes:profile?[profile.prototype]:[],count:{min:1,target:1,max:1,weight:1},minArea:0,placement:'boundary',interfaceType:''},bindExisting:false,bindInterfaces:false,bindTransport:false,profileIds:[],contentIds:[],maxCoverageDistance:null,sourceRefs:[]};}
    if(collection==='networks')return{id,description:'',kind:'power',scope:'station',priority:'required',segmentProfiles:[],terminals:[],allowSpace:false,maxLength:4096};
    return{id,controllerRuleId:program.devices?.[0]?.id||'',deviceRuleIds:[],scope:'room',component:'DeviceList',maxDevices:32,priority:'required'};
}
export function systemsReportHtml(report,layout,enabled){
    if(!enabled)return '<p class="node-small">Native infrastructure is disabled in this program.</p>';
    if(!report)return '<p class="node-small">Build service devices, then power, atmos and disposals.</p>';
    const terminals=report.terminals||[],reached=terminals.filter(item=>item.sourceReachable).length;
    return `<p><strong>Native infrastructure</strong><br>${(layout.systemDevices||[]).length} device bindings / ${(layout.systemNetworks||[]).length} networks / ${(layout.systemControls||[]).length} control links<br>${reached} of ${terminals.length} terminals have a witnessed source path.</p>`+terminals.map(item=>`<details class="system-terminal ${item.sourceReachable?'connected':'unconnected'}"><summary>${esc(item.networkId)} / ${esc(item.role)} / ${item.sourceReachable?'source reachable':'unverified'}</summary><p>${esc(item.deviceId)} / ${esc(item.portId)}</p><p class="node-small">${(item.witness||[]).map(esc).join(' &rarr; ')||'No source-path witness.'}${item.witnessTruncated?' (path display truncated by the configured witness budget)':''}</p></details>`).join('')+'<p class="node-small">Connectivity is evaluated from actual native port contacts. Simulation verified: no.</p>';
}
export function nativeSystemsSvg(project,interactive=false){
    const profiles=new Map((project.definition.systems?.profiles||[]).map(profile=>[profile.id,profile]));
    let html='<g data-layer="native-systems" pointer-events="'+(interactive?'all':'none')+'">';
    for(const network of project.layout.systemNetworks||[]){
        const rule=project.definition.systems?.networks?.find(rule=>rule.id===network.requirementId),color=rule?.kind==='gas'?'#ecb85e':rule?.kind==='disposal'?'#c99ce9':'#6fb9ff';
        html+='<g><title>'+esc(network.id)+'</title>';
        for(const segment of network.segments||[]){
            const profile=profiles.get(segment.profileId),x=segment.position.x+.5,y=segment.position.y+.5;
            const directions=new Map();
            for(const port of profile?.ports||[]){
                for(const name of port.connection==='cable'?['South','East','North','West']:(port.directions||[])){
                    let [dx,dy]=({south:[0,-1],east:[1,0],north:[0,1],west:[-1,0]})[name.toLowerCase()]||[0,0];
                    for(let turn=0;turn<((segment.rotation%360+360)%360)/90;turn++)[dx,dy]=[-dy,dx];
                    if(dx||dy)directions.set(dx+','+dy,[dx,dy]);
                }
            }
            html+=`<g data-system-segment="${esc(segment.id)}"><title>${esc(network.id+' / '+profile?.prototype+' / '+segment.rotation+' degrees')}</title>`;
            html+=[...directions.values()].map(([dx,dy])=>`<line x1="${x}" y1="${y}" x2="${x+dx*.5}" y2="${y+dy*.5}" stroke="${color}" stroke-width=".16"/>`).join('');
            html+=`<rect x="${x-.24}" y="${y-.24}" width=".48" height=".48" fill="${color}" opacity=".9"/></g>`;
        }
        html+='</g>';
    }
    return html+'</g>';
}
