import {detectPaperCorners} from './paper-detector.js';

self.onmessage = ({data}) => self.postMessage({points:detectPaperCorners(data.pixels,data.width,data.height),sourceWidth:data.sourceWidth,sourceHeight:data.sourceHeight});
