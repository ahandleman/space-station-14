import { renameProgramPath, removeProgramPath } from './hierarchy-editor.js';
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const copy = value => JSON.parse(JSON.stringify(value));
const priorities = [['required', 'Required'], ['desired', 'Desired'], ['avoided', 'Avoided']];
const relations = [['walking', 'Walking route'], ['adjacency', 'Shared boundary'], ['delivery', 'Delivery connection'], ['transport', 'Passenger transport']];
const purposes = [['crew', 'Crew'], ['patients', 'Patients'], ['items', 'Items / goods'], ['waste', 'Waste']];
const modes = [['publicHall', 'Public hall'], ['staffPassage', 'Staff passage'], ['maintenance', 'Maintenance'], ['disposalShortcut', 'Disposal passenger shortcut'], ['mail', 'Mail network']];
const colors = { required: '#8cdbc4', desired: '#e1b96c', avoided: '#e28b96' };
const label = (options, value) => options.find(([id]) => id === value)?.[1] || value;
const field = (title, body, hint = '') => `<label class="field">${escape(title)}${body}${hint ? `<small class="field-help">${escape(hint)}</small>` : ''}</label>`;
const select = (id, value, options, attributes = '') => `<select id="${id}" ${attributes}>${options.map(([v, title]) => `<option value="${escape(v)}" ${v === value ? 'selected' : ''}>${escape(title)}</option>`).join('')}</select>`;

export function createTopologyEditor({ getProject, record, changed, redraw, drawMap, notify, check, replaceTemplate, changeArchetype }) {
    let selectedNode = null;
    let selectedEdge = null;
    let draft = null;
    let filter = '';
    let priorityFilter = 'all';
    let connectMode = false;
    let connectFrom = null;
    let nodeDrag = null;
    let report = null;

    function topology() { return getProject().topology; }
    function normalize(edge) {
        return {
            ...edge,
            priority: edge.priority || (edge.hard === false ? 'desired' : 'required'),
            description: edge.description || '',
            relation: edge.relation || 'walking',
            purpose: edge.purpose || 'crew',
            allowedModes: edge.allowedModes || ['publicHall'],
            direction: edge.direction || 'both',
            strictAvoidance: edge.strictAvoidance || false,
            maxDistance: edge.maxDistance ?? null
        };
    }
    function newDraft(from = null, to = null) {
        const p = getProject();
        const nodes = p.topology?.nodes || [];
        const allowed = ['publicHall'];
        if (p.config.defaultAllowDisposalShortcuts) allowed.push('disposalShortcut');
        return {
            id: '', from: from || nodes[0] || '', to: to || nodes.find(n => n !== (from || nodes[0])) || '',
            priority: 'desired', weight: 3, description: '', relation: 'walking', purpose: 'crew',
            allowedModes: allowed, direction: 'both', strictAvoidance: false, maxDistance: null
        };
    }
    function edited() {
        report = null;
        changed();
    }
    function reset() {
        selectedNode = null; selectedEdge = null; draft = null; nodeDrag = null;
        connectMode = false; connectFrom = null; report = null;
    }
    function currentDraft() {
        if (!draft) draft = newDraft();
        return draft;
    }
    function pickEdge(id) {
        const edge = topology()?.edges.find(e => e.id === id);
        if (!edge) return;
        selectedEdge = id;
        selectedNode = null;
        draft = normalize(copy(edge));
        connectMode = false;
        connectFrom = null;
        redraw();
    }
    function pickNode(id) {
        if (connectMode) {
            if (!connectFrom) {
                connectFrom = id;
                selectedNode = id;
                notify(`Now select the department that ${id} should connect to.`);
            } else if (id !== connectFrom) {
                draft = newDraft(connectFrom, id);
                selectedEdge = null;
                connectMode = false;
                connectFrom = null;
                selectedNode = null;
            }
        } else {
            selectedNode = id;
            selectedEdge = null;
            draft = null;
        }
        redraw();
    }
    function positions() {
        const p = getProject();
        const nodes = p.topology?.nodes || [];
        const result = new Map();
        const hub = nodes.includes('Commons') ? 'Commons' : nodes[0];
        const remaining = p.config.archetype === 'hub' ? nodes.filter(n => n !== hub) : nodes;
        nodes.forEach(id => {
            const saved = p.topology?.nodePositions;
            if (saved && Object.hasOwn(saved, id)) {
                result.set(id, saved[id]);
                return;
            }
            if (p.config.archetype === 'hub' && id === hub) {
                result.set(id, { x: p.config.width / 2, y: p.config.height / 2 });
                return;
            }
            const i = remaining.indexOf(id);
            if (p.config.archetype === 'spine') {
                result.set(id, {
                    x: 18 + i * (p.config.width - 36) / Math.max(1, remaining.length - 1),
                    y: p.config.height / 2 + (i % 2 ? 18 : -18)
                });
            } else {
                const angle = i / remaining.length * Math.PI * 2 - Math.PI / 2;
                result.set(id, {
                    x: p.config.width / 2 + Math.cos(angle) * p.config.width * .34,
                    y: p.config.height / 2 + Math.sin(angle) * p.config.height * .34
                });
            }
        });
        return result;
    }

    function inspectorHtml() {
        const p = getProject();
        const t = p.topology;
        let html = '<p class="description">Choose the station structure, then describe how departments should relate. Select a line to edit its dependency or connect two departments directly.</p>';
        html += '<div class="section-label">STATION TOPOLOGY</div><div class="archetypes">';
        for (const [id, icon, name] of [['hub','⊛','Hub'],['ring','⊡','Ring'],['spine','╪','Spine']])
            html += `<button data-topology-action="archetype" data-value="${id}" class="${p.config.archetype === id ? 'selected' : ''}" title="Choose ${name} structure; your dependencies stay in place"><b>${icon}</b>${name}</button>`;
        html += '</div>';
        html += field('Generation seed', `<input id="topology-seed" type="number" value="${p.config.seed}" min="-2147483648" max="2147483647">`);
        html += `<div class="tools topology-tools"><button data-topology-action="new-edge">+ Dependency</button><button data-topology-action="connect" class="${connectMode ? 'selected' : ''}">${connectMode ? 'Cancel connect' : '↗ Connect nodes'}</button></div>`;
        if (connectMode) html += `<p class="notice">${connectFrom ? `Choose the second department after ${escape(connectFrom)}.` : 'Click a department, then click its destination.'}</p>`;
        html += `<div class="section-label">DEPARTMENTS <button data-topology-action="add-node" class="text-button">+ Add</button></div><div class="departments">`;
        for (const d of p.config.departments) {
            const count = t?.edges.filter(e => e.from === d.id || e.to === d.id).length || 0;
            html += `<button class="department-row ${selectedNode === d.id ? 'selected' : ''}" data-topology-node="${escape(d.id)}"><i style="background:${/^#[0-9a-f]{6}$/i.test(d.color) ? d.color : '#6e90b0'}"></i><span>${escape(d.id)}</span><small>${count} links</small></button>`;
        }
        html += '</div>';
        html += `<details><summary>New dependency defaults</summary><p class="muted">These defaults apply to new dependencies. Existing links keep their individual settings.</p><label class="toggle policy-toggle"><input id="default-shortcuts" type="checkbox" ${p.config.defaultAllowDisposalShortcuts ? 'checked' : ''}> Allow passenger disposal shortcuts</label><label class="toggle policy-toggle"><input id="default-mail" type="checkbox" ${p.config.defaultAllowMailNetwork ? 'checked' : ''}> Allow mail for item delivery</label></details>`;
        if (t) {
            const edges = t.edges.map(normalize).filter(e => (priorityFilter === 'all' || e.priority === priorityFilter) &&
                `${e.from} ${e.to} ${e.description}`.toLowerCase().includes(filter.toLowerCase()));
            html += `<div class="section-label">DEPENDENCIES <span>${edges.length} / ${t.edges.length}</span></div><div class="dependency-filters"><input id="dependency-search" type="search" placeholder="Find department or description" aria-label="Search dependencies" value="${escape(filter)}">${select('dependency-priority-filter', priorityFilter, [['all','All priorities'],...priorities])}</div><div class="dependency-list">`;
            for (const e of edges)
                html += `<div class="dependency-card ${e.priority} ${selectedEdge === e.id ? 'selected' : ''}"><button class="dependency-pick" data-topology-edge="${escape(e.id)}"><span>${escape(e.from)} ${e.direction === 'forward' ? '→' : '↔'} ${escape(e.to)}</span><small><b>${label(priorities,e.priority)}</b> · weight ${e.weight} · ${label(relations,e.relation)}</small>${e.description ? `<em>${escape(e.description)}</em>` : ''}</button><button class="dependency-remove" data-delete-dependency="${escape(e.id)}" aria-label="Remove ${escape(e.from)} to ${escape(e.to)} dependency" title="Remove dependency">×</button></div>`;
            if (!edges.length) html += '<p class="muted">No matching dependencies. Add one or clear the filters.</p>';
            html += '</div><div class="separator"></div><button data-topology-action="reset-template" class="full">Replace links with topology template</button><p class="field-help">Replaces the dependency list. Undo restores your previous plan.</p>';
        }
        return html;
    }

    function detailsHtml() {
        const p = getProject();
        if (selectedNode) {
            const node = p.config.departments.find(d => d.id === selectedNode);
            if (!node) { selectedNode = null; return detailsHtml(); }
            return `<div class="section-label">DEPARTMENT</div>${field('Name', `<input id="topology-node-name" value="${escape(node.id)}" maxlength="48">`)}${field('Description / role', `<textarea id="topology-node-description" rows="3" maxlength="2000" placeholder="What does this department provide?">${escape(node.description || '')}</textarea>`)}<button data-topology-action="save-node" class="primary full">Apply department changes</button><div class="separator"></div><button data-topology-action="node-link" class="full">+ Dependency from ${escape(node.id)}</button><button data-topology-action="remove-node" class="full danger node-remove">Remove department and its links</button><p class="muted">Removing a department removes its incoming and outgoing dependencies. Undo restores both.</p>`;
        }
        const d = currentDraft();
        const nodes = p.topology?.nodes.map(id => [id,id]) || [];
        return `<div class="section-label">${selectedEdge ? 'EDIT DEPENDENCY' : 'NEW DEPENDENCY'}<button data-topology-action="new-edge" class="text-button">+ New</button></div>
            <div class="field-row">${field('From', select('edge-from',d.from,nodes,'data-dependency-field="from"'))}${field('To', select('edge-to',d.to,nodes,'data-dependency-field="to"'))}</div>
            <div class="priority-buttons" role="group" aria-label="Dependency priority">${priorities.map(([id,name]) => `<button data-dependency-priority="${id}" class="${id} ${d.priority === id ? 'selected' : ''}">${name}</button>`).join('')}</div>
            <p class="priority-explanation">${d.priority === 'required' ? 'Must be satisfied. Weight never makes a required dependency optional.' : d.priority === 'desired' ? 'Prefer this relationship. Higher weight gives it more influence.' : 'Discourage this relationship. Higher weight strengthens the separation preference.'}</p>
            ${field('Description / reason', `<textarea id="edge-description" data-dependency-field="description" rows="3" maxlength="2000" placeholder="Explain what this dependency should achieve…">${escape(d.description)}</textarea>`)}
            <div class="field-row">${field('Relationship',select('edge-relation',d.relation,relations,'data-dependency-field="relation"'))}${field('Who / what moves',select('edge-purpose',d.purpose,purposes,'data-dependency-field="purpose"'))}</div>
            ${field('Direction',select('edge-direction',d.direction,[['both','Both directions ↔'],['forward','From → To']],'data-dependency-field="direction"'))}
            <div class="section-label">WEIGHT <output id="edge-weight-output">${d.weight}</output></div>
            <div class="weight-controls"><input id="edge-weight-slider" type="range" min="0.1" max="10" step="0.1" value="${Math.min(10,d.weight)}" aria-label="Dependency weight slider"><input id="edge-weight" data-dependency-field="weight" type="number" min="0.1" max="1000" step="0.1" value="${d.weight}" aria-label="Dependency weight"></div>
            <div class="weight-presets"><button data-dependency-weight="1">Low · 1</button><button data-dependency-weight="3">Normal · 3</button><button data-dependency-weight="8">High · 8</button></div>
            ${field(d.priority === 'avoided' ? 'Preferred minimum separation (tiles)' : 'Maximum route distance (optional)', `<input id="edge-distance" data-dependency-field="maxDistance" type="number" min="1" max="4096" value="${d.maxDistance ?? ''}" placeholder="No distance limit">`)}
            ${d.priority === 'avoided' ? `<label class="toggle policy-toggle"><input id="edge-strict" data-dependency-field="strictAvoidance" type="checkbox" ${d.strictAvoidance ? 'checked' : ''}> Never allow this relationship</label>` : ''}
            <fieldset class="mode-options"><legend>Allowed connection methods</legend>${modes.map(([id,name]) => `<label class="toggle"><input id="edge-mode-${id}" type="checkbox" data-dependency-mode="${id}" ${d.allowedModes.includes(id) ? 'checked' : ''} ${!d.allowedModes.includes(id) && (id === 'mail' && d.purpose !== 'items' || id === 'disposalShortcut' && ['patients','waste'].includes(d.purpose)) ? 'disabled' : ''}>${name}</label>`).join('')}</fieldset>
            <p class="field-help">Mail is for items. Passenger shortcuts and mail describe future physical networks; topology checks validate these requirements, not their construction.</p>
            <div id="dependency-error" class="inline-error" role="alert"></div>
            <div class="dependency-form-actions"><button id="save-dependency" data-topology-action="save-edge" class="primary">${selectedEdge ? 'Apply changes' : '+ Add dependency'}</button>${selectedEdge ? `<button data-delete-dependency="${escape(selectedEdge)}" class="danger">Remove</button>` : ''}</div>
            <p class="field-help">Select a line or a dependency card to edit it. Line thickness shows weight.</p>`;
    }

    function graphHtml() {
        const t = topology();
        if (!t) return '';
        const points = positions();
        const defs = new Map(getProject().config.departments.map(d => [d.id,d]));
        let html = `<defs>${Object.entries(colors).map(([name,color]) => `<marker id="arrow-${name}" viewBox="0 0 10 10" refX="24" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0 0L10 5L0 10Z" fill="${color}"/></marker>`).join('')}</defs>`;
        const groups = new Map();
        for (const e of t.edges) {
            const pair = [e.from,e.to].sort().join('\0');
            if (!groups.has(pair)) groups.set(pair,[]);
            groups.get(pair).push(e.id);
        }
        for (const raw of t.edges) {
            const e = normalize(raw);
            const a = points.get(e.from), b = points.get(e.to);
            if (!a || !b) continue;
            const ids = groups.get([e.from,e.to].sort().join('\0'));
            const offset = (ids.indexOf(e.id) - (ids.length-1)/2) * 7;
            const distance = Math.hypot(b.x-a.x,b.y-a.y) || 1;
            const orientation = e.from < e.to ? 1 : -1;
            const mid = { x:(a.x+b.x)/2-(b.y-a.y)/distance*offset*orientation, y:(a.y+b.y)/2+(b.x-a.x)/distance*offset*orientation };
            const lx=(a.x+2*mid.x+b.x)/4, ly=(a.y+2*mid.y+b.y)/4;
            const active = selectedEdge === e.id;
            const stroke = colors[e.priority];
            const dash = e.priority === 'required' ? '0' : e.priority === 'desired' ? '1.8 1' : '.5 1.2';
            const width = Math.min(1.25,.25+Math.log2(e.weight+1)*.16);
            const name = `${e.from} ${e.direction === 'forward' ? 'to' : 'and'} ${e.to}: ${label(priorities,e.priority)}, weight ${e.weight}. ${e.description}`;
            html += `<g data-topology-edge="${escape(e.id)}" class="topology-link ${active ? 'selected' : ''}" tabindex="0" role="button" aria-label="${escape(name)}"><title>${escape(name)}</title><path d="M${a.x},${a.y} Q${mid.x},${mid.y} ${b.x},${b.y}" fill="none" stroke="transparent" stroke-width="4"/><path d="M${a.x},${a.y} Q${mid.x},${mid.y} ${b.x},${b.y}" fill="none" stroke="${active?'#f1f4dc':stroke}" stroke-width="${active?width+.4:width}" stroke-dasharray="${dash}" ${e.direction==='forward'?`marker-end="url(#arrow-${e.priority})"`:''}/><text x="${lx}" y="${ly-1}" text-anchor="middle" fill="${stroke}" font-size="1.65" style="paint-order:stroke;stroke:#121f29;stroke-width:.7px">${label(priorities,e.priority)} ×${e.weight}</text></g>`;
        }
        for (const [id,p] of points) {
            const definition = defs.get(id);
            const color = /^#[0-9a-f]{6}$/i.test(definition?.color) ? definition.color : '#8fb9ab';
            const selected = id === selectedNode || id === connectFrom;
            const width = Math.max(14, Math.min(27,id.length*1.25+5));
            html += `<g class="topology-node ${selected?'selected':''}" data-topology-node="${escape(id)}" tabindex="0" role="button" aria-label="${escape(id)} department"><title>${escape(definition?.description || id)}</title><rect x="${p.x-width/2}" y="${p.y-4}" width="${width}" height="8" rx="1.2" fill="${selected?'#304d43':'#1c303a'}" stroke="${selected?'#e5f2d4':color}" stroke-width="${selected?.7:.45}"/><text x="${p.x}" y="${p.y+.7}" text-anchor="middle" fill="#e1eae9" font-size="2.05">${escape(id)}</text></g>`;
        }
        return html;
    }

    function saveEdge() {
        const d = copy(currentDraft());
        const error = document.getElementById('dependency-error');
        const reject = message => { if(error)error.textContent=message; notify(message,true); };
        if (!d.from || !d.to || d.from === d.to) return reject('Choose two different departments.');
        if (!Number.isFinite(d.weight) || d.weight < .1 || d.weight > 1000) return reject('Weight must be between 0.1 and 1000.');
        if (!d.allowedModes.length) return reject('Select at least one allowed connection method.');
        if (d.allowedModes.includes('mail') && d.purpose !== 'items') return reject('Mail can fulfill item delivery, not crew or patient movement.');
        if (d.allowedModes.includes('disposalShortcut') && ['patients','waste'].includes(d.purpose)) return reject('Patient access and waste routing need suitable passages or their own networks.');
        if (d.maxDistance != null && (!Number.isInteger(d.maxDistance) || d.maxDistance < 1 || d.maxDistance > 4096)) return reject('Distance must be 1–4096 tiles or blank.');
        const same = topology().edges.map(normalize).find(e => e.id !== selectedEdge && e.priority === d.priority && e.relation === d.relation && e.purpose === d.purpose && e.direction === d.direction &&
            JSON.stringify([...e.allowedModes].sort())===JSON.stringify([...d.allowedModes].sort()) &&
            (e.from === d.from && e.to === d.to || d.direction === 'both' && e.from === d.to && e.to === d.from));
        if (same) return reject('That dependency already exists. Select it to edit its weight and description.');
        record();
        d.id = selectedEdge || `dependency-${crypto.randomUUID()}`;
        d.origin = 'custom';
        d.hard = d.priority === 'required';
        if (d.priority !== 'avoided') d.strictAvoidance=false;
        const index = topology().edges.findIndex(e => e.id === selectedEdge);
        if (index >= 0) { clearLegacyDefault(topology().edges[index]); topology().edges[index]=d; }
        else topology().edges.push(d);
        selectedEdge=d.id; draft=copy(d); selectedNode=null; edited(); redraw();
        notify(index >= 0 ? 'Dependency updated.' : 'Dependency added.');
    }
    function removeEdge(id) {
        const edge=topology().edges.find(e=>e.id===id);
        if (!edge)return;
        record(); clearLegacyDefault(edge); topology().edges=topology().edges.filter(e=>e.id!==id);
        if(id.startsWith('template-'))topology().suppressedTemplateIds=[...new Set([...(topology().suppressedTemplateIds||[]),id])];
        if(selectedEdge===id){selectedEdge=null;draft=null;}
        edited();redraw();notify('Dependency removed. Undo restores it.');
    }
    function clearLegacyDefault(edge) {
        for(const d of getProject().config.departments){
            const other=d.id===edge.from?edge.to:d.id===edge.to?edge.from:null;
            if(other==null)continue;
            d.requiredConnections=(d.requiredConnections||[]).filter(id=>id!==other);
            d.preferredNeighbors=(d.preferredNeighbors||[]).filter(id=>id!==other);
        }
    }
    function addNode() {
        const p=getProject();
        if(p.config.departments.length>=16){notify('This prototype supports up to 16 departments.',true);return;}
        let n=1;while(p.config.departments.some(d=>d.id===`Department ${n}`))n++;
        const id=`Department ${n}`;record();
        p.config.departments.push({id,description:'',width:12,height:10,minArea:40,maxArea:p.config.departments[0].maxArea,access:'public',exterior:false,entrances:1,color:'#6e90b0',requiredConnections:[],preferredNeighbors:[]});
        topology().nodes.push(id);selectedNode=id;selectedEdge=null;draft=null;edited();redraw();
    }
    function saveNode() {
        const p=getProject(),node=p.config.departments.find(d=>d.id===selectedNode);
        const name=document.getElementById('topology-node-name').value.trim();
        if(!/^[A-Za-z0-9][A-Za-z0-9 _-]{0,47}$/.test(name)||p.config.departments.some(d=>d!==node&&d.id===name)){notify('Choose a unique ID of 1–48 letters, numbers, spaces, underscores or hyphens.',true);return;}
        record();const old=node.id;node.id=name;node.description=document.getElementById('topology-node-description').value;
        renameProgramPath(p,old,name);
        topology().nodes=topology().nodes.map(id=>id===old?name:id);
        for(const edge of topology().edges){if(edge.from===old||edge.to===old)edge.origin='custom';if(edge.from===old)edge.from=name;if(edge.to===old)edge.to=name;}
        for(const d of p.config.departments){d.requiredConnections=d.requiredConnections.map(id=>id===old?name:id);d.preferredNeighbors=d.preferredNeighbors.map(id=>id===old?name:id);}
        topology().nodePositions=Object.fromEntries(Object.entries(topology().nodePositions||{}).map(([id,pos])=>[id===old?name:id,pos]));
        selectedNode=name;edited();redraw();notify('Department updated; its dependencies were preserved.');
    }
    function removeNode() {
        const p=getProject();if(p.config.departments.length<=3){notify('Keep at least three departments in this prototype.',true);return;}
        const id=selectedNode;record();p.config.departments=p.config.departments.filter(d=>d.id!==id);
        removeProgramPath(p,id);
        topology().nodes=topology().nodes.filter(n=>n!==id);topology().edges=topology().edges.filter(e=>e.from!==id&&e.to!==id);
        topology().nodePositions=Object.fromEntries(Object.entries(topology().nodePositions||{}).filter(([name])=>name!==id));
        for(const d of p.config.departments){d.requiredConnections=d.requiredConnections.filter(n=>n!==id);d.preferredNeighbors=d.preferredNeighbors.filter(n=>n!==id);}
        selectedNode=null;draft=null;edited();redraw();notify('Department and its dependencies removed.');
    }
    function handleClick(event) {
        const target=event.target.closest('[data-topology-action],[data-topology-node],[data-topology-edge],[data-delete-dependency],[data-dependency-priority],[data-dependency-weight]');
        if(!target)return false;
        if(target.dataset.topologyNode){pickNode(target.dataset.topologyNode);return true;}
        if(target.dataset.topologyEdge){pickEdge(target.dataset.topologyEdge);return true;}
        if(target.dataset.deleteDependency){removeEdge(target.dataset.deleteDependency);return true;}
        if(target.dataset.dependencyPriority){currentDraft().priority=target.dataset.dependencyPriority;redraw();return true;}
        if(target.dataset.dependencyWeight){currentDraft().weight=+target.dataset.dependencyWeight;redraw();return true;}
        switch(target.dataset.topologyAction){
            case 'new-edge':selectedNode=null;selectedEdge=null;draft=newDraft();connectMode=false;redraw();break;
            case 'connect':connectMode=!connectMode;connectFrom=null;redraw();break;
            case 'save-edge':saveEdge();break;
            case 'add-node':addNode();break;
            case 'save-node':saveNode();break;
            case 'remove-node':removeNode();break;
            case 'node-link':draft=newDraft(selectedNode);selectedNode=null;selectedEdge=null;redraw();break;
            case 'archetype':changeArchetype(target.dataset.value);break;
            case 'reset-template':replaceTemplate();break;
        }
        return true;
    }
    function updateDraft(target) {
        const d=currentDraft(),key=target.dataset.dependencyField;
        if(key){
            d[key]=target.type==='checkbox'?target.checked:key==='weight'?+target.value:key==='maxDistance'?(target.value===''?null:+target.value):target.value;
            if(key==='purpose'){
                if(d.purpose!=='items')d.allowedModes=d.allowedModes.filter(m=>m!=='mail');
                else if(getProject().config.defaultAllowMailNetwork&&!d.allowedModes.includes('mail'))d.allowedModes.push('mail');
                if(['patients','waste'].includes(d.purpose))d.allowedModes=d.allowedModes.filter(m=>m!=='disposalShortcut');
            }
            return true;
        }
        const mode=target.dataset.dependencyMode;
        if(mode){d.allowedModes=d.allowedModes.filter(m=>m!==mode);if(target.checked)d.allowedModes.push(mode);return true;}
        return false;
    }
    function handleChange(event) {
        const target=event.target;
        if(updateDraft(target)){if(target.dataset.dependencyField==='purpose')redraw();return true;}
        if(target.id==='dependency-priority-filter'){priorityFilter=target.value;redraw();return true;}
        if(target.id==='dependency-search'){filter=target.value;redraw();return true;}
        if(target.id==='default-shortcuts'||target.id==='default-mail'){
            record();getProject().config[target.id==='default-shortcuts'?'defaultAllowDisposalShortcuts':'defaultAllowMailNetwork']=target.checked;redraw();return true;
        }
        if(target.id==='topology-seed'){
            const seed=+target.value;if(!Number.isInteger(seed)||seed< -2147483648||seed>2147483647){notify('Enter a valid 32-bit seed.',true);return true;}
            record();getProject().config.seed=seed;edited();redraw();return true;
        }
        return false;
    }
    function handleInput(event) {
        const target=event.target;
        if(target.id==='edge-weight-slider'||target.id==='edge-weight'){
            currentDraft().weight=+target.value;
            document.getElementById('edge-weight-output').textContent=target.value;
            document.getElementById(target.id==='edge-weight'?'edge-weight-slider':'edge-weight').value=target.id==='edge-weight'?Math.min(10,+target.value):target.value;
            return true;
        }
        return updateDraft(target);
    }
    function pointerDown(event,point) {
        const node=event.target.closest('[data-topology-node]');
        if(!node)return false;
        const id=node.dataset.topologyNode;
        if(connectMode)return false;
        const position=positions().get(id);
        nodeDrag={id,start:point,position,changed:false};
        return true;
    }
    function pointerMove(point) {
        if(!nodeDrag)return false;
        const dx=Math.round(point.x-nodeDrag.start.x),dy=Math.round(point.y-nodeDrag.start.y);
        if(!nodeDrag.changed&&!dx&&!dy)return true;
        if(!nodeDrag.changed){record();nodeDrag.changed=true;}
        const c=getProject().config;
        const pos={x:Math.max(10,Math.min(c.width-10,Math.round(nodeDrag.position.x+dx))),y:Math.max(7,Math.min(c.height-7,Math.round(nodeDrag.position.y+dy)))};
        topology().nodePositions=Object.fromEntries([...Object.entries(topology().nodePositions||{}).filter(([id])=>id!==nodeDrag.id),[nodeDrag.id,pos]]);
        drawMap();return true;
    }
    function pointerUp(cancelled=false){const previous=nodeDrag;nodeDrag=null;if(previous&&!previous.changed&&!cancelled)pickNode(previous.id);return previous?.changed;}
    function handleKey(event){
        const target=event.target.closest('[data-topology-node],[data-topology-edge]');
        if(event.key==='Enter'&&target){event.preventDefault();handleClick({target});return true;}
        if(event.key==='Delete'&&selectedEdge&&event.target.closest('#canvas')){event.preventDefault();removeEdge(selectedEdge);return true;}
        return false;
    }
    function summary(){
        const edges=topology()?.edges.map(normalize)||[];
        return `<div class="dependency-summary">${priorities.map(([id,name])=>`<span class="${id}"><b>${edges.filter(e=>e.priority===id).length}</b>${name}</span>`).join('')}</div>`;
    }
    return {
        inspectorHtml,detailsHtml,graphHtml,handleClick,handleChange,handleInput,handleKey,pointerDown,pointerMove,pointerUp,reset,summary,
        clearReport(){report=null;}, getReport(){return report;},
        async validate(){report=await check();redraw();},
        getHelp(){return connectMode?'Select two departments to start a dependency':'Drag departments to arrange · select lines to edit · scroll to zoom';}
    };
}
