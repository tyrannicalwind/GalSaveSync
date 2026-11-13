// Local in-memory WebDAV fixture. Never connects to an external service.
const http = require('http');
const crypto = require('crypto');
const base = '/dav/测试 空格/';
const dirs = new Set([base,base+'unsafe/',base+'weak/',base+'noetag/',base+'redirect/']);
const files = new Map();
const xml = s => s.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');
const encoded = p => p.split('/').map(encodeURIComponent).join('/');
const server = http.createServer(async (req,res) => {
  if (req.headers.authorization !== 'Basic ' + Buffer.from('tester:test-only').toString('base64')) { res.writeHead(401);res.end();return; }
  let p; try { p = decodeURIComponent(new URL(req.url,'http://localhost').pathname); } catch { res.writeHead(400);res.end();return; }
  if (!p.startsWith(base)) {res.writeHead(404);res.end();return;}
  if (p.startsWith(base+'redirect/')) {res.writeHead(302,{'Location':'https://example.com/'});res.end();return;}
  if (req.method === 'MKCOL') {
    if (dirs.has(p)) {res.writeHead(405);res.end();return;}
    const parent = p.replace(/\/$/,'').slice(0,p.replace(/\/$/,'').lastIndexOf('/')+1);
    if (!dirs.has(parent)) {res.writeHead(409);res.end();return;}
    dirs.add(p);res.writeHead(201);res.end();return;
  }
  if (req.method === 'PROPFIND') {
    if (!dirs.has(p)) {res.writeHead(404);res.end();return;}
    const children = [...dirs,...files.keys()].filter(d => d === p || (d.startsWith(p) && !d.slice(p.length).replace(/\/$/,'').includes('/')));
    res.writeHead(207,{'Content-Type':'application/xml'});
    res.end('<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">' + children.map(d => '<d:response><d:href>' + xml(encoded(d)) + '</d:href><d:propstat><d:prop><d:resourcetype>'+ (dirs.has(d) ? '<d:collection/>' : '') +'</d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>').join('') + '</d:multistatus>');return;
  }
  if (req.method === 'GET') {
    const f = files.get(p); if (!f) {res.writeHead(404);res.end();return;}
    const headers = {'Content-Length':f.data.length};
    if (!p.startsWith(base+'noetag/')) headers.ETag = p.startsWith(base+'weak/') ? 'W/'+f.etag : f.etag;
    res.writeHead(200,headers);res.end(f.data);return;
  }
  if (req.method === 'PUT') {
    const chunks=[];for await (const chunk of req) chunks.push(chunk); const data=Buffer.concat(chunks);
    if (!dirs.has(p.slice(0,p.lastIndexOf('/')+1))) {res.writeHead(409);res.end();return;}
    const old=files.get(p);
    if (!p.startsWith(base+'unsafe/') && !p.startsWith(base+'noetag/') && ((req.headers['if-none-match']==='*' && old) || (req.headers['if-match'] && (!old || old.etag!==req.headers['if-match'])))) {res.writeHead(412);res.end();return;}
    files.set(p,{data,etag:'"'+crypto.createHash('sha256').update(data).digest('hex')+'"'});res.writeHead(201);res.end();return;
  }
  if (req.method === 'DELETE') { files.delete(p);res.writeHead(204);res.end();return; }
  res.writeHead(405);res.end();
});
server.listen(0,'127.0.0.1',() => console.log('READY http://127.0.0.1:'+server.address().port+encoded(base)));
