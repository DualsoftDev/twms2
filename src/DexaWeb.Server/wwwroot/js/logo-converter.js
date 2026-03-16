// PNG/JPG → SVG 변환 (imagetracer.js 기반)
window.logoConverter = {
    convertToSvg: function (base64Data, mimeType) {
        return new Promise(function (resolve, reject) {
            var img = new Image();
            img.onload = function () {
                var canvas = document.createElement('canvas');
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
                var ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0);
                var imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
                var options = {
                    numberofcolors: 32,
                    colorsampling: 2,
                    pathomit: 4,
                    ltres: 1,
                    qtres: 1,
                    scale: 1,
                    desc: false,
                    viewbox: true
                };
                try {
                    var svgStr = ImageTracer.imagedataToSVG(imageData, options);
                    // viewBox에서 width/height 추출 후 SVG 루트에 명시적으로 추가
                    var vbMatch = svgStr.match(/viewBox="0 0 (\d+(?:\.\d+)?) (\d+(?:\.\d+)?)"/);
                    if (vbMatch) {
                        svgStr = svgStr.replace('<svg ', '<svg width="' + vbMatch[1] + '" height="' + vbMatch[2] + '" ');
                    }
                    resolve(svgStr);
                } catch (e) {
                    reject(e);
                }
            };
            img.onerror = function () { reject(new Error('이미지 로드 실패')); };
            img.src = 'data:' + mimeType + ';base64,' + base64Data;
        });
    }
};
