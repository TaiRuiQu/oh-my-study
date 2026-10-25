const paperNeighbors = [[1,0],[-1,0],[0,1],[0,-1]];
function paperCross(a,b,c) { return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]); }
function paperArea(points) {
    return Math.abs(points.reduce((sum,point,index) => {
        const next = points[(index+1)%points.length]; return sum+point[0]*next[1]-next[0]*point[1];
    },0))/2;
}
function paperHull(points) {
    points.sort((a,b) => a[0]-b[0] || a[1]-b[1]);
    const hull = [];
    for (const point of points) {
        while (hull.length >= 2 && paperCross(hull[hull.length-2],hull[hull.length-1],point) <= 0) hull.pop();
        hull.push(point);
    }
    const lower = hull.length;
    for (let index = points.length-2; index >= 0; index--) {
        const point = points[index];
        while (hull.length > lower && paperCross(hull[hull.length-2],hull[hull.length-1],point) <= 0) hull.pop();
        hull.push(point);
    }
    hull.pop(); return hull;
}
function paperQuadrilateral(mask,width,height,allowClipped = false) {
    const visited = new Uint8Array(mask.length), queue = new Int32Array(mask.length);
    let best = null, largestArea = width*height*.15;
    for (let start = 0; start < mask.length; start++) {
        if (!mask[start] || visited[start]) continue;
        let read = 0, write = 1; queue[0] = start; visited[start] = 1;
        const boundary = [];
        while (read < write) {
            const point = queue[read++], x = point%width, y = Math.floor(point/width);
            let edge = false;
            for (const [dx,dy] of paperNeighbors) {
                const nx = x+dx, ny = y+dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) { edge = true; continue; }
                const next = ny*width+nx;
                if (!mask[next]) { edge = true; continue; }
                if (!visited[next]) { visited[next] = 1; queue[write++] = next; }
            }
            if (edge) boundary.push([x,y]);
        }
        if (write < width+height || boundary.length < 4) continue;
        const hull = paperHull(boundary), hullArea = paperArea(hull);
        if (hullArea <= largestArea || hullArea >= (width-1)*(height-1)*.98) continue;
        while (hull.length > 4) {
            let remove = 0, smallest = Infinity;
            for (let index = 0; index < hull.length; index++) {
                const triangle = Math.abs(paperCross(hull[(index+hull.length-1)%hull.length],hull[index],hull[(index+1)%hull.length]));
                if (triangle < smallest) { smallest = triangle; remove = index; }
            }
            hull.splice(remove,1);
        }
        const area = paperArea(hull);
        if (hull.length !== 4 || area < hullArea*.88 || area <= largestArea) continue;
        const clipped = hull.filter(point => point[0] <= 1 || point[0] >= width-2 || point[1] <= 1 || point[1] >= height-2).length;
        if (clipped > (allowClipped ? 2 : 0)) continue;
        if (hull.some((point,index) => Math.hypot(point[0]-hull[(index+1)%4][0],point[1]-hull[(index+1)%4][1]) < Math.min(width,height)*.15)) continue;
        let first = 0;
        for (let index = 1; index < 4; index++) if (hull[index][0]+hull[index][1] < hull[first][0]+hull[first][1]) first = index;
        best = Array.from({length:4},(_,index) => hull[(index+first)%4]); largestArea = area;
    }
    return best;
}
function paperMorphology(mask,width,height,radius,dilate) {
    const temporary = new Uint8Array(mask.length), result = new Uint8Array(mask.length);
    for (let y = 0; y < height; y++) {
        let count = 0, total = 0;
        for (let x = 0; x <= radius && x < width; x++) { count += mask[y*width+x]; total++; }
        for (let x = 0; x < width; x++) {
            temporary[y*width+x] = (dilate ? count > 0 : count === total) ? 1 : 0;
            if (x-radius >= 0) { count -= mask[y*width+x-radius]; total--; }
            if (x+radius+1 < width) { count += mask[y*width+x+radius+1]; total++; }
        }
    }
    for (let x = 0; x < width; x++) {
        let count = 0, total = 0;
        for (let y = 0; y <= radius && y < height; y++) { count += temporary[y*width+x]; total++; }
        for (let y = 0; y < height; y++) {
            result[y*width+x] = (dilate ? count > 0 : count === total) ? 1 : 0;
            if (y-radius >= 0) { count -= temporary[(y-radius)*width+x]; total--; }
            if (y+radius+1 < height) { count += temporary[(y+radius+1)*width+x]; total++; }
        }
    }
    return result;
}
function refinePaperCorners(corners,gray,affinity,width,height,mask) {
    const lines = [], limit = Math.min(width,height)*.16;
    const sample = (data,x,y) => data[((y+.5)|0)*width+((x+.5)|0)];
    for (let edge = 0; edge < 4; edge++) {
        const a = corners[edge], b = corners[(edge+1)%4], dx = b[0]-a[0], dy = b[1]-a[1], length = Math.hypot(dx,dy);
        const middleX = (a[0]+b[0])/2, middleY = (a[1]+b[1])/2, direction = Math.atan2(dy,dx);
        const clipped = (a[0] <= 1 && b[0] <= 1) || (a[0] >= width-2 && b[0] >= width-2) || (a[1] <= 1 && b[1] <= 1) || (a[1] >= height-2 && b[1] >= height-2);
        const band = clipped ? limit : Math.min(width,height)*.07;
        let best = {nx:-dy/length,ny:dx/length,c:(-dy*a[0]+dx*a[1])/length,score:0,offset:0};
        const candidates = [];
        for (let angle = -6; angle <= 6; angle += 2) {
            const theta = direction+angle*Math.PI/180, ux = Math.cos(theta), uy = Math.sin(theta), nx = -uy, ny = ux;
            for (let offset = clipped ? 0 : -band; offset <= band; offset++) {
                const cx = middleX+nx*offset, cy = middleY+ny*offset;
                let score = 0, count = 0, supported = 0;
                for (let step = 0; step < 64; step++) {
                    const distance = (step/63-.5)*length*.84, x = cx+ux*distance, y = cy+uy*distance;
                    const ix = x+nx*2, iy = y+ny*2, ox = x-nx*2, oy = y-ny*2;
                    if (ix < 0 || ox < 0 || ix >= width-1 || ox >= width-1 || iy < 0 || oy < 0 || iy >= height-1 || oy >= height-1) continue;
                    const color = sample(affinity,ix,iy)-sample(affinity,ox,oy), tone = sample(gray,ix,iy)-sample(gray,ox,oy);
                    const raw = (color > 0 ? color : 0)*.7+(tone < 0 ? -tone : tone)*.35, energy = raw > 100 ? 100 : raw;
                    score += energy; count++; if (energy > 6) supported++;
                }
                if (count < 40) continue;
                const candidate = {nx,ny,c:nx*cx+ny*cy,score:score/count,offset,support:supported/count};
                candidates.push(candidate);
                if (candidate.score > best.score) best = candidate;
            }
        }
        if (clipped) {
            const enclosed = candidates.filter(candidate => candidate.offset > 3 && candidate.score >= best.score*.25 && candidate.support >= .65);
            if (enclosed.length) {
                const farthest = Math.max(...enclosed.map(candidate => candidate.offset));
                best = enclosed.filter(candidate => candidate.offset >= farthest-2).reduce((current,candidate) => candidate.score > current.score ? candidate : current);
            }
            else best = {nx:-dy/length,ny:dx/length,c:(-dy*a[0]+dx*a[1])/length};
        }
        lines.push(best);
    }
    const refined = [];
    for (let index = 0; index < 4; index++) {
        const a = lines[(index+3)%4], b = lines[index], determinant = a.nx*b.ny-b.nx*a.ny;
        if (Math.abs(determinant) < .15) return corners;
        const point = [(a.c*b.ny-b.c*a.ny)/determinant,(a.nx*b.c-b.nx*a.c)/determinant];
        if (point[0] < -limit || point[0] > width+limit || point[1] < -limit || point[1] > height+limit) return corners;
        point[0] = Math.max(0,Math.min(width-1,point[0])); point[1] = Math.max(0,Math.min(height-1,point[1]));
        if (!mask[((point[1]+.5)|0)*width+((point[0]+.5)|0)]) {
            let closest = point, distance = Math.min(width,height)*.065;
            for (let y = Math.max(0,Math.floor(point[1]-distance)); y <= Math.min(height-1,point[1]+distance); y++)
                for (let x = Math.max(0,Math.floor(point[0]-distance)); x <= Math.min(width-1,point[0]+distance); x++) {
                    if (!mask[y*width+x]) continue;
                    const length = Math.hypot(x-point[0],y-point[1]);
                    if (length < distance) { distance = length; closest = [x,y]; }
                }
            refined.push(closest);
        } else refined.push(point);
    }
    if (paperArea(refined) < paperArea(corners)*.65 || refined.some((point,index) => paperCross(point,refined[(index+1)%4],refined[(index+2)%4]) <= 0)) return corners;
    return refined;
}
export function detectPaperCorners(pixels,width,height) {
    if (width < 3 || height < 3 || pixels.length !== width*height*4) return null;
    const gray = new Uint8Array(width*height), histogram = new Uint32Array(256), affinity = new Uint8Array(width*height);
    let colorMask = new Uint8Array(width*height);
    for (let index = 0; index < gray.length; index++) {
        const pixel = index*4, value = (pixels[pixel]*77+pixels[pixel+1]*150+pixels[pixel+2]*29)>>8;
        gray[index] = value; histogram[value]++;
        const high = Math.max(pixels[pixel],pixels[pixel+1],pixels[pixel+2]), low = Math.min(pixels[pixel],pixels[pixel+1],pixels[pixel+2]);
        colorMask[index] = high > 70 && high-low < high*.14+4 ? 1 : 0;
        affinity[index] = Math.round(Math.max(0,1-(high-low)/Math.max(1,high*.3))*Math.min(1,high/100)*255);
    }
    const closeRadius = 1, openRadius = Math.max(2,Math.round(Math.min(width,height)/80));
    colorMask = paperMorphology(paperMorphology(colorMask,width,height,closeRadius,true),width,height,closeRadius,false);
    colorMask = paperMorphology(paperMorphology(colorMask,width,height,openRadius,false),width,height,openRadius,true);
    const colorQuad = paperQuadrilateral(colorMask,width,height,true);
    if (colorQuad) return refinePaperCorners(colorQuad,gray,affinity,width,height,colorMask).map(point => [point[0]/(width-1),point[1]/(height-1)]);
    let sum = 0, lowSum = 0, lowCount = 0, threshold = 127, bestVariance = 0;
    for (let value = 0; value < 256; value++) sum += value*histogram[value];
    for (let value = 0; value < 255; value++) {
        lowCount += histogram[value]; lowSum += value*histogram[value];
        const highCount = gray.length-lowCount;
        if (!lowCount || !highCount) continue;
        const difference = lowSum/lowCount-(sum-lowSum)/highCount, variance = lowCount*highCount*difference*difference;
        if (variance > bestVariance) { bestVariance = variance; threshold = value; }
    }
    if (!bestVariance) return null;
    const mask = new Uint8Array(gray.length);
    for (let index = 0; index < gray.length; index++) mask[index] = gray[index] > threshold ? 1 : 0;
    let quad = paperQuadrilateral(mask,width,height);
    if (!quad) {
        mask.fill(0);
        for (let y = 1; y < height-1; y++) for (let x = 1; x < width-1; x++) {
            const pixel = y*width+x;
            const gx = -gray[pixel-width-1]+gray[pixel-width+1]-2*gray[pixel-1]+2*gray[pixel+1]-gray[pixel+width-1]+gray[pixel+width+1];
            const gy = -gray[pixel-width-1]-2*gray[pixel-width]-gray[pixel-width+1]+gray[pixel+width-1]+2*gray[pixel+width]+gray[pixel+width+1];
            if (Math.abs(gx)+Math.abs(gy) < 160) continue;
            for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) mask[pixel+dy*width+dx] = 1;
        }
        quad = paperQuadrilateral(mask,width,height);
    }
    return quad?.map(point => [point[0]/(width-1),point[1]/(height-1)]) || null;
}
export function mapPaperCorners(points,sourceWidth,sourceHeight,displayWidth,displayHeight) {
    const scale = Math.max(displayWidth/sourceWidth,displayHeight/sourceHeight);
    const offsetX = (sourceWidth*scale-displayWidth)/2, offsetY = (sourceHeight*scale-displayHeight)/2;
    return points.map(point => [(point[0]*sourceWidth*scale-offsetX)/displayWidth,(point[1]*sourceHeight*scale-offsetY)/displayHeight]);
}
