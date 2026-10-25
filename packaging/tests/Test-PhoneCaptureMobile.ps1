$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$node = Get-Command node -ErrorAction Stop
$testDirectory = Join-Path $workspace 'output/phone-capture-tests'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = @'
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import test from 'node:test';
import {dirname,join} from 'node:path';
import vm from 'node:vm';

const html = readFileSync(__CAPTURE_HTML__, 'utf8');
const scriptsDirectory = dirname(__CAPTURE_HTML__);
const detector = readFileSync(join(scriptsDirectory,'paper-detector.js'),'utf8').replace(/^export /gm,'');
const capture = readFileSync(join(scriptsDirectory,'capture.js'),'utf8').replace(/^import .*;\r?\n/gm,'');
const workerScript = readFileSync(join(scriptsDirectory,'paper-worker.js'),'utf8').replace(/^import .*;\r?\n/gm,'');
const script = detector+'\n'+capture;

async function setup({fail = new Set(), hold = new Set(), secure = true, nativePhoto = false, photoFailure = false, docres = false, cameraFrame = null, failOrder = false, useWorker = false} = {}) {
    class Element {
        listeners = new Map();
        addEventListener(name,callback) { this.listeners.set(name,callback); }
        setPointerCapture() {}
        closest(selector) { return selector === '.page' && this.className === 'page' ? this : null; }
        children = [];
        style = {};
        dataset = {};
        classList = {toggle() {}, add() {}, remove() {}};
        hidden = false;
        value = '';
        videoWidth = 1200;
        videoHeight = 1600;
        naturalWidth = 1200;
        naturalHeight = 1600;
        readyState = 4;
        clientWidth = 390;
        clientHeight = 718;
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.children = children; }
        attributes = new Map();
        setAttribute(name, value) { this.attributes.set(name,String(value)); this[name] = name === 'hidden' ? true : value; }
        toggleAttribute(name, force) { if (force) this.attributes.set(name,''); else this.attributes.delete(name); this[name] = force; }
        hasAttribute(name) { return this.attributes.has(name); }
        inputs = [];
        querySelectorAll(selector) { return this.inputs.filter(input => !selector.includes(':checked') || input.checked).filter(input => !selector.includes('data-process') || input.dataset.process); }
        querySelector(selector) { return this.inputs.find(input => selector.includes('data-process="'+input.dataset.process+'"')) || null; }
        getContext() { return {drawImage() {}, translate() {}, rotate() {}, getImageData: (_,__,width,height) => ({data:cameraFrame?.pixels || new Uint8ClampedArray(width*height*4)})}; }
        getBoundingClientRect() { return {width:this.clientWidth,height:this.clientHeight}; }
        toBlob(callback) { callback(new Blob(['synthetic'], {type: 'image/jpeg'})); }
        play() { return Promise.resolve(); }
        decode() { return Promise.resolve(); }
        showModal() { this.open = true; }
        close() { this.open = false; return this.onclose?.(); }
    }
    const elements = new Map(), calls = [], files = [], cameraRequests = [], photoRequests = [], cameraErrors = [];
    const timers = new Map(), timeouts = new Map(), workers = []; let nextTimer = 0;
    const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]));
    function element(id) {
        if (!ids.has(id)) return null;
        if (!elements.has(id)) {
            const value = new Element();
            const tag = html.match(new RegExp('<[^>]+\\bid="'+id+'"[^>]*>'))?.[0] || '';
            if (/\shidden(?:\s|>|=)/.test(tag)) value.setAttribute('hidden','');
            const fieldset = html.match(new RegExp('<fieldset id="'+id+'"[^>]*>([\\s\\S]*?)</fieldset>'));
            if (fieldset) for (const match of fieldset[1].matchAll(/<input\b([^>]*)>/g)) {
                const input = new Element();
                input.dataset.process = match[1].match(/data-process="([^"]+)"/)?.[1];
                input.value = match[1].match(/value="([^"]+)"/)?.[1] || '';
                input.checked = /\bchecked\b/.test(match[1]);
                value.inputs.push(input);
            }
            if (id === 'capture-configuration') value.textContent = JSON.stringify({strings:{},language:'en-US'});
            elements.set(id, value);
        }
        return elements.get(id);
    }
    const track = {stop() { this.stopped = true; }, applyConstraints() { throw new Error('Preview constraints must stay unchanged when toggling HD'); }};
    const context = vm.createContext({
        document: {body: {dataset: {}}, hidden: false, documentElement: {}, title: '', querySelectorAll: () => [],
            getElementById: element,
            createElement: () => new Element(), createElementNS: () => new Element(), addEventListener() {}},
        window: {isSecureContext: secure, scrollY:0, innerHeight:844, scrollBy() {}, scrollTo() {}, addEventListener() {}},
        requestAnimationFrame: callback => callback(),
        setTimeout: (callback,delay) => { const id = ++nextTimer; timeouts.set(id,{callback,delay}); return id; }, clearTimeout: id => timeouts.delete(id),
        location: {hash: '#synthetic-session'},
        navigator: {mediaDevices: {getUserMedia: async options => { cameraRequests.push(options); return {getTracks: () => [track], getVideoTracks: () => [track]}; }}},
        URL: {createObjectURL: () => 'blob:synthetic', revokeObjectURL() {}},
        Blob, File, AbortController, console: {...console, error: (...args) => cameraErrors.push(args)}, Image: Element,
        setInterval: (callback,delay) => { const id = ++nextTimer; timers.set(id,{callback,delay}); return id; }, clearInterval: id => timers.delete(id),
        cameraTestFrame: cameraFrame,
        fetch: async (path, options = {}) => {
            calls.push({path, options});
            if (options.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
            if (path === '/status') return Response.json({files, restoration: {eligible: docres, installed: docres, enabled: docres}});
            if (path.startsWith('/upload?')) {
                const info = {id: 'p'+files.length, name: decodeURIComponent(path.split('=')[1]), corners: [[0,0],[1,0],[1,1],[0,1]], rotation: 0};
                info.pdf = info.name.endsWith('.pdf'); files.push(info); return Response.json(info);
            }
            if (path.startsWith('/render/')) {
                const id = path.split('/')[2];
                if (hold.has(id)) await new Promise((_, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), {once: true}));
                if (fail.has(id)) return new Response('', {status: 422});
                return new Response(new Blob(['corrected']));
            }
            if (path === '/reorder' && failOrder) return new Response('',{status:400});
            return new Response('');
        }
    });
    if (useWorker) context.Worker = class {
        constructor(url,options) { assert.equal(url,'/paper-worker.js'); assert.equal(options.type,'module'); workers.push(this); }
        postMessage(data,transfers) { this.data = data; assert.equal(transfers[0],data.pixels.buffer); }
        reply() {
            const workerContext = vm.createContext({self:{postMessage: data => this.onmessage({data})}});
            vm.runInContext(detector+'\n'+workerScript,workerContext);
            workerContext.self.onmessage({data:this.data});
        }
        terminate() { this.terminated = true; }
    };
    if (nativePhoto) context.ImageCapture = class {
        constructor(source) { assert.equal(source, track); }
        async getPhotoCapabilities() { return {imageWidth: {max: 4000}}; }
        async takePhoto(options) {
            photoRequests.push(options);
            if (photoFailure) throw new Error('Synthetic native photo failure');
            return new Blob(['native-photo'], {type: 'image/png'});
        }
    };
    vm.runInContext(script, context);
    await new Promise(resolve => setImmediate(resolve));
    const run = code => vm.runInContext(code, context);
    return {run, elements, calls, track, cameraRequests, photoRequests, cameraErrors, timers, timeouts, workers, context};
}

function paperFrame(width,height,corners,{paper = 235,background = 35,text = true} = {}) {
    const pixels = new Uint8ClampedArray(width*height*4);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        const inside = corners.every((point,index) => {
            const next = corners[(index+1)%4]; return (next[0]-point[0])*(y/(height-1)-point[1])-(next[1]-point[1])*(x/(width-1)-point[0]) >= 0;
        });
        let value = inside ? paper : background;
        if (inside && text && x > width*.3 && x < width*.65 && y > height*.3 && y < height*.7 && y%16 < 2) value = 60;
        const offset = (y*width+x)*4; pixels[offset] = pixels[offset+1] = pixels[offset+2] = value; pixels[offset+3] = 255;
    }
    return {pixels,width,height};
}

test('classic browser corner detection follows skewed paper and printed content', async () => {
    for (const corners of [
        [[.18,.12],[.82,.2],[.88,.86],[.12,.82]],
        [[.48,.08],[.88,.38],[.55,.92],[.12,.62]]
    ]) {
        const frame = paperFrame(240,320,corners), app = await setup({cameraFrame:frame});
        const detected = app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)');
        assert.equal(detected?.length, 4);
        for (let index = 0; index < 4; index++) assert.ok(Math.hypot(detected[index][0]-corners[index][0],detected[index][1]-corners[index][1]) < .025);
    }
});

test('paper color and boundary refinement reject wood, separate keyboard keys and tolerate a shadow', async () => {
    const corners = [[.12,.18],[.9,.16],[.92,.9],[.1,.93]], frame = paperFrame(320,240,corners);
    for (let y = 0; y < frame.height; y++) for (let x = 0; x < frame.width; x++) {
        const offset = (y*frame.width+x)*4;
        if (frame.pixels[offset] === 35) {
            frame.pixels[offset] = 155+x%7; frame.pixels[offset+1] = 104; frame.pixels[offset+2] = 52;
            if (y < 25 && x%32 < 28) frame.pixels[offset] = frame.pixels[offset+1] = frame.pixels[offset+2] = 230;
        } else {
            const shadow = .6+.4*x/frame.width;
            for (let channel = 0; channel < 3; channel++) frame.pixels[offset+channel] *= shadow;
        }
    }
    const app = await setup({cameraFrame:frame});
    const detected = app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)');
    assert.equal(detected?.length,4);
    for (let index = 0; index < 4; index++) assert.ok(Math.hypot(detected[index][0]-corners[index][0],detected[index][1]-corners[index][1]) < .035);
});

test('Sobel fallback detects a darker page against a brighter background', async () => {
    const corners = [[.18,.12],[.82,.2],[.88,.86],[.12,.82]];
    const frame = paperFrame(240,320,corners,{paper:65,background:225,text:false}), app = await setup({cameraFrame:frame});
    const detected = app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)');
    assert.equal(detected?.length, 4);
    for (let index = 0; index < 4; index++) assert.ok(Math.hypot(detected[index][0]-corners[index][0],detected[index][1]-corners[index][1]) < .035);
});

test('blank frames, gradients and round objects are rejected while clipped pages remain detectable', async () => {
    const frame = paperFrame(160,200,[[0,0],[1,0],[1,1],[0,1]],{paper:180,text:false});
    const app = await setup({cameraFrame:frame});
    assert.equal(app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)'), null);
    for (let y = 0; y < frame.height; y++) for (let x = 0; x < frame.width; x++) {
        const offset = (y*frame.width+x)*4;
        frame.pixels[offset] = frame.pixels[offset+1] = frame.pixels[offset+2] = 60+Math.round(x/frame.width*120);
    }
    assert.equal(app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)'), null);
    for (let y = 0; y < frame.height; y++) for (let x = 0; x < frame.width; x++) {
        const offset = (y*frame.width+x)*4, inside = ((x-80)/55)**2+((y-100)/75)**2 < 1;
        frame.pixels[offset] = frame.pixels[offset+1] = frame.pixels[offset+2] = inside ? 235 : 35;
    }
    assert.equal(app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)'), null);
    const clipped = paperFrame(160,200,[[0,0],[.9,.1],[.85,.9],[0,1]],{text:false});
    frame.pixels = clipped.pixels;
    assert.equal(app.run('detectPaperCorners(cameraTestFrame.pixels,cameraTestFrame.width,cameraTestFrame.height)').length, 4);
});

test('live outline maps video cover cropping, updates with paper motion and clears after losing the page', async () => {
    const corners = [[.18,.12],[.82,.2],[.88,.86],[.12,.82]];
    const frame = paperFrame(240,320,corners), app = await setup({cameraFrame:frame});
    await app.run('startCamera()');
    app.run('updateFramingGuides()');
    assert.equal(app.elements.get('framing-guides').hidden, false);
    assert.equal(app.elements.get('framing-guides').hasAttribute('hidden'), false);
    const points = app.elements.get('live-boundary').points.split(' ').map(point => point.split(',').map(Number));
    const scale = 718/1600, offset = (1200*scale-390)/2;
    for (let index = 0; index < 4; index++) {
        assert.ok(Math.abs(points[index][0]/100-(corners[index][0]*1200*scale-offset)/390) < .025);
        assert.ok(Math.abs(points[index][1]/100-corners[index][1]) < .025);
    }
    const first = app.elements.get('live-boundary').points;
    frame.pixels = paperFrame(240,320,[[.25,.16],[.85,.22],[.9,.84],[.2,.8]]).pixels;
    app.run('updateFramingGuides()');
    assert.notEqual(app.elements.get('live-boundary').points, first);
    const portrait = app.elements.get('live-boundary').points;
    app.elements.get('live').clientWidth = 844; app.elements.get('live').clientHeight = 264;
    app.run('updateFramingGuides()');
    assert.notEqual(app.elements.get('live-boundary').points, portrait);
    frame.pixels.fill(180); app.run('updateFramingGuides()');
    assert.equal(app.elements.get('framing-guides').hidden, true);
    assert.equal(app.elements.get('framing-guides').hasAttribute('hidden'), true);
    assert.equal(app.calls.some(call => call.path.startsWith('/upload')), false);
});

test('live boundary detection stops outside capture and resumes without reopening the camera', async () => {
    const app = await setup();
    await app.run('startCamera()');
    assert.equal([...app.timers.values()].filter(timer => timer.delay === 400).length, 1);
    app.run('show("tray")');
    assert.equal([...app.timers.values()].filter(timer => timer.delay === 400).length, 0);
    assert.equal(app.elements.get('framing-guides').hidden, true);
    app.run('show("capture")');
    assert.equal([...app.timers.values()].filter(timer => timer.delay === 400).length, 1);
    assert.equal(app.cameraRequests.length, 1);
    assert.equal(app.track.stopped, undefined);
    app.run('stopCamera()');
    assert.equal([...app.timers.values()].filter(timer => timer.delay === 400).length, 0);
    assert.equal(app.elements.get('framing-guides').hidden, true);
});

test('HD toggles preserve the preview stream and capture native photos only at the shutter', async () => {
    const app = await setup({nativePhoto: true});
    app.run('$("hd-camera").onclick()');
    await app.run('startCamera()');
    assert.equal(app.cameraRequests[0].video.width.ideal, 1920);
    assert.equal(app.cameraRequests[0].video.height.ideal, 1440);
    assert.equal(app.photoRequests.length, 0);
    const previewStream = app.elements.get('live').srcObject;
    app.run('$("hd-camera").onclick(); $("hd-camera").onclick()');
    assert.equal(app.elements.get('live').srcObject, previewStream);
    assert.equal(app.cameraRequests.length, 1);
    assert.equal(app.track.stopped, undefined);
    await app.run('takePhoto();');
    await app.run('takePhoto();');
    assert.equal(app.photoRequests.length, 2);
    assert.equal(app.photoRequests[0].imageWidth, 4000);
    assert.equal(await app.run('items[0].file.text()'), 'native-photo');
    assert.equal(app.run('items[0].file.name'), 'Scan-01.png');
    assert.equal(app.run('view'), 'capture');
    assert.equal(app.track.stopped, undefined);
    app.run('$("hd-camera").onclick()');
    await app.run('takePhoto()');
    assert.equal(app.photoRequests.length, 2);
    assert.equal(await app.run('items[2].file.text()'), 'synthetic');
});

test('failed native HD capture saves a standard photo and leaves preview and queue usable', async () => {
    const app = await setup({nativePhoto: true, photoFailure: true});
    await app.run('startCamera()');
    const previewStream = app.elements.get('live').srcObject;
    app.run('$("hd-camera").onclick()');
    await app.run('takePhoto()');
    assert.equal(app.run('items.length'), 1);
    assert.equal(app.run('highResolution'), false);
    assert.equal(app.elements.get('hd-camera')['aria-pressed'], 'false');
    assert.equal(app.elements.get('live').srcObject, previewStream);
    assert.equal(app.track.stopped, undefined);
    assert.match(app.elements.get('status').textContent, /standard photo was saved/);
    assert.match(app.cameraErrors[0][1].message, /Synthetic native photo failure/);
    await app.run('takePhoto()');
    assert.equal(app.run('items.length'), 2);
    assert.equal(app.photoRequests.length, 1);
});

test('unsupported native HD capture keeps standard capture available', async () => {
    const app = await setup();
    await app.run('startCamera()');
    app.run('$("hd-camera").onclick()');
    assert.equal(app.run('highResolution'), false);
    assert.match(app.elements.get('status').textContent, /Use system camera/);
    assert.equal(app.track.stopped, undefined);
    await app.run('takePhoto()');
    assert.equal(app.run('items.length'), 1);
});

test('continuous shutter keeps camera open and queues locally before review', async () => {
    const app = await setup();
    await app.run('startCamera()');
    await app.run('takePhoto()');
    await app.run('takePhoto()');
    await app.run('takePhoto()');
    assert.equal(app.run('items.length'), 3);
    assert.equal(app.run('view'), 'capture');
    assert.equal(app.track.stopped, undefined);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 0);
    await app.run('openTray(); worker');
    assert.equal(app.run('items.every(item => item.state === "ready")'), true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 3);
    assert.equal(app.calls.filter(call => call.path.startsWith('/render')).length, 3);
    for (const call of app.calls.filter(call => call.path.startsWith('/render'))) {
        const correction = JSON.parse(call.options.body);
        assert.deepEqual(correction.tasks, ['dewarping','deshadowing','appearance','deblurring']);
        assert.deepEqual(correction.processing, {crop:true,autoOrientation:true,shadows:true,deblur:true,appearance:true});
        assert.deepEqual(correction.corners, []);
    }
    assert.equal(app.calls.filter(call => call.path.startsWith('/accept')).length, 0);
    await app.run('$("send-batch").onclick()');
    assert.deepEqual(JSON.parse(app.calls.find(call => call.path === '/accept-batch').options.body).ids, ['p0','p1','p2']);
    assert.equal(app.run('items.every(item => item.sent)'), true);
});

test('failed preview can be deselected and retried without uploading its original again', async () => {
    const fail = new Set(['p1']), app = await setup({fail});
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg"),new File(["c"],"three.jpg")])');
    await app.run('openTray(); worker');
    assert.equal(app.run('items[1].state'), 'error');
    assert.equal(app.run('items[0].state === "ready" && items[2].state === "ready"'), true);
    app.run('items[1].selected = false; updateActions()');
    assert.equal(app.elements.get('send-batch').disabled, false);
    await app.run('$("send-batch").onclick()');
    assert.deepEqual(JSON.parse(app.calls.find(call => call.path === '/accept-batch').options.body).ids, ['p0','p2']);
    fail.clear();
    await app.run('items[1].state = "local"; prepareAll()');
    assert.equal(app.run('items[1].state'), 'ready');
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 3);
});

test('canceling previews preserves queued originals and permits retry', async () => {
    const hold = new Set(['p0']), app = await setup({hold});
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg")]); openTray()');
    await new Promise(resolve => setImmediate(resolve));
    await app.run('batchAbort.abort(); worker');
    assert.equal(app.run('items.length === 2 && items[0].file.size > 0 && items[1].file.size > 0'), true);
    assert.equal(app.run('items[0].state'), 'local');
    assert.equal(app.run('preparing'), false);
    hold.clear(); await app.run('prepareAll()');
    assert.equal(app.run('items.every(item => item.state === "ready")'), true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 2);
});

test('HTTP camera fallback still allows several files to queue without processing', async () => {
    const app = await setup({secure: false});
    await app.run('startCamera()');
    assert.match(app.elements.get('status').textContent, /trusted HTTPS/);
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.pdf")])');
    assert.equal(app.run('items.length'), 2);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 0);
});

test('failed manual correction cannot send a stale preview and retains the edits for retry', async () => {
    const fail = new Set(), app = await setup({fail});
    app.run('addFiles([new File(["a"],"one.jpg")])');
    await app.run('openTray(); worker');
    fail.add('p0');
    app.run('active = items[0]; editing = true; corners = [[0,0],[1,0],[1,1],[0,1]]; rotation = 90; $("backend").value = "standard"');
    await app.run('savePage()');
    assert.equal(app.run('items[0].state'), 'error');
    assert.equal(app.run('items[0].correction.rotation'), 90);
    assert.equal(app.elements.get('send-batch').disabled, true);
    fail.clear(); await app.run('items[0].state = "local"; prepareAll()');
    assert.equal(app.run('items[0].state'), 'ready');
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 1);
});

test('processing defaults retain arbitrary independent selections without adding dewarping', async () => {
    const app = await setup();
    app.run('defaultProcessing = {shadows:true,grayscale:true}; addFiles([new File(["a"],"one.jpg")])');
    await app.run('openTray(); worker');
    const correction = JSON.parse(app.calls.find(call => call.path.startsWith('/render')).options.body);
    assert.deepEqual(correction.processing, {shadows:true,grayscale:true});
    assert.deepEqual(correction.tasks, ['dewarping','deshadowing','appearance','deblurring']);
    assert.equal(correction.processing.rectify, undefined);
    assert.equal(correction.processing.crop, undefined);
});

test('global processing changes rerender every unsent photo and also apply to future photos', async () => {
    const app = await setup();
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg"),new File(["c"],"three.jpg"),new File(["pdf"],"four.pdf")])');
    await app.run('openTray(); worker');
    app.run('items[0].sent = true; items[0].selected = false; items[2].selected = false; items[1].correction.rotation = 90; items[1].correction.corners = [[.1,.1],[.9,.1],[.9,.9],[.1,.9]]');
    const sentCorrection = app.run('JSON.stringify(items[0].correction)');
    app.run('$("capture-processing").querySelector(\'[data-process="shadows"]\').checked = false; $("capture-processing").querySelector(\'[data-process="grayscale"]\').checked = true');
    await app.run('captureOptionsChanged()');
    assert.equal(app.run('JSON.stringify(items[0].correction)'), sentCorrection);
    assert.equal(app.run('items[1].correction.processing.shadows'), false);
    assert.equal(app.run('items[2].correction.processing.grayscale'), true);
    assert.equal(app.run('items[1].correction.rotation'), 90);
    assert.equal(app.run('items[1].correction.corners[0][0]'), .1);
    assert.equal(app.run('items[2].selected'), false);
    assert.equal(app.run('items[3].correction'), undefined);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 4);
    assert.equal(app.calls.filter(call => call.path.startsWith('/render')).length, 5);
    app.run('addFiles([new File(["new"],"five.jpg")])');
    await app.run('worker');
    assert.equal(app.run('items[4].correction.processing.shadows'), false);
    assert.equal(app.run('items[4].correction.processing.grayscale'), true);
    await app.run('$("reset-capture-processing").onclick()');
    assert.equal(app.run('items.filter(item => !item.sent && !item.pdf).every(item => item.state === "ready" && item.correction.rotation === 0 && !Object.values(item.correction.processing).some(Boolean) && item.correction.tasks.length === 0)'), true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 5);
    assert.equal(app.run('JSON.stringify(items[0].correction)'), sentCorrection);
});

test('global DocRes selection starts with every task and can disable a task for the whole batch', async () => {
    const app = await setup({docres:true});
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg")])');
    await app.run('openTray(); worker');
    app.run('$("capture-backend").value = "docres"');
    await app.run('captureOptionsChanged()');
    const renders = app.calls.filter(call => call.path.startsWith('/render')).slice(-2);
    for (const render of renders) {
        const correction = JSON.parse(render.options.body);
        assert.equal(correction.backend, 'docres');
        assert.deepEqual(correction.corners, []);
        assert.deepEqual(correction.tasks, ['dewarping','deshadowing','deblurring','appearance']);
    }
    app.run('$("capture-docres-tasks").querySelectorAll("input")[0].checked = false');
    await app.run('captureOptionsChanged()');
    assert.equal(app.run('items.every(item => item.correction.backend === "docres" && !item.correction.tasks.includes("dewarping") && item.correction.tasks.length === 3)'), true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/upload')).length, 2);
});

test('manual corners override automatic cropping and full-photo selection turns cropping off', async () => {
    const app = await setup();
    app.run('addFiles([new File(["a"],"one.jpg")])');
    await app.run('openTray(); worker');
    await app.run('beginEdit()');
    assert.equal(app.run('automaticCrop'), true);
    app.run('corners = [[.1,.1],[.9,.1],[.9,.9],[.1,.9]]; enableManualCrop()');
    await app.run('savePage()');
    assert.equal(app.run('items[0].correction.corners[0][0]'), .1);
    await app.run('beginEdit()');
    app.run('$("whole").onclick()');
    await app.run('savePage()');
    assert.equal(app.run('items[0].correction.processing.crop'), false);
    assert.equal(app.run('items[0].correction.corners.length'), 4);
});

test('restoration tool opens global settings and batches all-page changes until the sheet closes', async () => {
    const app = await setup();
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg")])');
    await app.run('openTray(); worker');
    app.run('$("restoration-tool").onclick()');
    assert.equal(app.elements.get('capture-options').open, true);
    app.run('$("capture-processing").querySelector(\'[data-process="shadows"]\').checked = false');
    await app.run('captureOptionsChanged()');
    app.run('$("capture-processing").querySelector(\'[data-process="deblur"]\').checked = false');
    await app.run('captureOptionsChanged()');
    assert.equal(app.run('items.every(item => item.state === "local")'), true);
    assert.equal(app.elements.get('send-batch').disabled, true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/render')).length, 2);
    await app.run('$("close-options").onclick()');
    assert.equal(app.run('items.every(item => item.state === "ready" && !item.correction.processing.shadows && !item.correction.processing.deblur)'), true);
    assert.equal(app.calls.filter(call => call.path.startsWith('/render')).length, 4);
});
test('boundary worker avoids overlapping requests and stops after leaving capture', async () => {
    const frame = paperFrame(240,320,[[.18,.12],[.82,.2],[.88,.86],[.12,.82]]);
    const app = await setup({cameraFrame:frame,useWorker:true});
    await app.run('startCamera()');
    app.run('updateFramingGuides()');
    const worker = app.workers[0];
    assert.equal(app.run('boundaryBusy'),true);
    const request = worker.data;
    app.run('updateFramingGuides()'); assert.equal(worker.data,request);
    worker.reply();
    assert.equal(app.run('boundaryBusy'),false);
    assert.equal(app.elements.get('framing-guides').hidden,false);
    assert.equal(app.timers.size,2);
    app.run('show("tray")'); assert.equal(worker.terminated,true);
    assert.equal(app.elements.get('framing-guides').hidden,true);
});

function pointer(stage,x,y,id = 1) {
    return {pointerId:id,pointerType:'touch',button:0,clientX:x,clientY:y,target:stage};
}
async function threePages(options) {
    const app = await setup(options);
    app.run('addFiles([new File(["a"],"one.jpg"),new File(["b"],"two.jpg"),new File(["c"],"three.jpg")])');
    await app.run('openTray(); worker');
    return app;
}
test('losing implicit touch capture mid-swipe does not cancel the page change', async () => {
    const app = await threePages(), stage = app.elements.get('review-stage');
    stage.onpointerdown(pointer(stage,300,200)); stage.onpointermove(pointer(stage,280,205));
    stage.onlostpointercapture?.(pointer(stage,280,205));
    stage.onpointermove(pointer(stage,80,215)); stage.onpointerup(pointer(stage,80,215));
    assert.equal(app.run('reviewIndex'),1);
    assert.equal(stage.style.transform,'');
});
test('vertical scroll, short gestures, canceled touches and selection controls do not turn pages', async () => {
    const app = await threePages(), stage = app.elements.get('review-stage');
    stage.onpointerdown(pointer(stage,300,200)); stage.onpointermove(pointer(stage,280,300)); stage.onpointerup(pointer(stage,280,300));
    stage.onpointerdown(pointer(stage,300,200)); stage.onpointermove(pointer(stage,200,205));
    stage.onpointerdown(pointer(stage,150,250,2)); stage.onpointerup(pointer(stage,150,250,2)); stage.onpointerup(pointer(stage,200,205));
    stage.onpointerdown(pointer(stage,300,200)); stage.onpointermove(pointer(stage,280,205)); stage.onpointerup(pointer(stage,280,205));
    stage.onpointerdown(pointer(stage,300,200)); stage.onpointermove(pointer(stage,80,205)); stage.onpointercancel(pointer(stage,80,205)); stage.onpointerup(pointer(stage,80,205));
    stage.onpointerdown({...pointer(stage,300,200),target:{closest:()=>true}}); stage.onpointermove(pointer(stage,80,205)); stage.onpointerup(pointer(stage,80,205));
    assert.equal(app.run('reviewIndex'),0); assert.equal(stage.style.transform,'');
});
test('pinching zooms the photo around the gesture, panning moves it and page swipes stay disabled while zoomed', async () => {
    const app = await threePages(), stage = app.elements.get('review-stage'), image = app.elements.get('tray-result');
    stage.onpointerdown(pointer(stage,195,300,1)); stage.onpointerdown(pointer(stage,195,500,2));
    stage.onpointermove(pointer(stage,195,200,1));
    assert.equal(app.run('photoView.scale'),1.5);
    assert.match(image.style.transform,/scale\(1\.5\)/);
    stage.onpointermove(pointer(stage,195,600,2));
    assert.equal(app.run('photoView.scale'),2);
    assert.equal(stage.style.transform,'');
    stage.onpointerup(pointer(stage,195,200,1));
    const zoomed = image.style.transform;
    stage.onpointermove(pointer(stage,150,550,2));
    assert.notEqual(image.style.transform,zoomed);
    stage.onpointerup(pointer(stage,150,550,2));
    stage.onpointerdown(pointer(stage,300,400,3)); stage.onpointermove(pointer(stage,100,405,3)); stage.onpointerup(pointer(stage,100,405,3));
    assert.equal(app.run('reviewIndex'),0);
    assert.equal(stage.style.transform,'');
    assert.equal(app.run('photoView.scale'),2);
    stage.onpointerdown(pointer(stage,195,200,4)); stage.onpointerdown(pointer(stage,195,600,5));
    stage.onpointermove(pointer(stage,195,300,4)); stage.onpointermove(pointer(stage,195,500,5));
    stage.onpointerup(pointer(stage,195,300,4)); stage.onpointerup(pointer(stage,195,500,5));
    assert.equal(app.run('photoView.scale'),1);
    assert.equal(image.style.transform,'');
});
test('long press drags pages, keeps their settings and sends the saved order before confirmation', async () => {
    const app = await threePages(); app.run('setReviewLayout("grid"); items[0].selected = false');
    const rows = app.elements.get('pages').children, preview = rows[0].children[0].children[0];
    app.context.document.elementFromPoint = () => rows[2];
    const touch = (x,y) => ({touches:[{clientX:x,clientY:y}],preventDefault() { this.prevented = true; }});
    preview.listeners.get('touchstart')(touch(100,150));
    const hold = [...app.timeouts.values()].find(timer => timer.delay === 450); assert.ok(hold); hold.callback();
    const move = touch(300,450); preview.listeners.get('touchmove')(move); assert.equal(move.prevented,true);
    const end = touch(300,450); preview.listeners.get('touchend')(end);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.run('items.map(item => item.name).join(",")'),'two.jpg,three.jpg,one.jpg');
    assert.equal(app.run('items[2].selected'),false);
    assert.equal(app.run('currentPage().name'),'one.jpg');
    assert.deepEqual(JSON.parse(app.calls.find(call => call.path === '/reorder').options.body).ids,['p1','p2','p0']);
    await app.run('$("send-batch").onclick()');
    const accept = app.calls.findIndex(call => call.path === '/accept-batch');
    assert.equal(app.calls[accept-1].path,'/reorder');
    assert.deepEqual(JSON.parse(app.calls[accept].options.body).ids,['p1','p2']);
});
test('scrolling before a long press and canceled drags do not reorder; failed saves roll back', async () => {
    const app = await threePages({failOrder:true}); app.run('setReviewLayout("grid")');
    const rows = app.elements.get('pages').children, preview = rows[0].children[0].children[0];
    const touch = y => ({touches:[{clientX:100,clientY:y}],preventDefault() {}});
    preview.listeners.get('touchstart')(touch(150)); preview.listeners.get('touchmove')(touch(170));
    assert.equal(app.run('pageDrag'),null);
    preview.listeners.get('touchstart')(touch(150));
    [...app.timeouts.values()].find(timer => timer.delay === 450).callback();
    preview.listeners.get('touchcancel')(touch(150)); assert.equal(app.run('pageDrag'),null);
    assert.equal(app.calls.some(call => call.path === '/reorder'),false);
    await app.run('reorderPages(items[0],items[2])');
    assert.equal(app.run('items.map(item => item.name).join(",")'),'one.jpg,two.jpg,three.jpg');
    assert.equal(app.run('reordering'),false);
    assert.equal(app.elements.get('send-batch').disabled,false);
});

'@
$sourcePath = Join-Path $workspace 'src/OhMyStudy.App/MobileCapture/index.html'
$testSource = $testSource.Replace('__CAPTURE_HTML__', ($sourcePath | ConvertTo-Json -Compress))
$testPath = Join-Path $testDirectory 'mobile.test.mjs'
[IO.File]::WriteAllText($testPath, $testSource.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
& $node.Source --test $testPath
if ($LASTEXITCODE) { throw 'Phone capture mobile interaction checks failed' }

