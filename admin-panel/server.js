const express = require('express');
const https = require('https');
const http = require('http');
const { Server } = require('socket.io');
const cors = require('cors');
const fs = require('fs');
const path = require('path');
const { spawn, exec } = require('child_process');
const crypto = require('crypto');
const session = require('express-session');
const bcrypt = require('bcryptjs');

const app = express();

// Configuration paths
const CONFIG = {
    gameDir: '/home/death/techtonica-server/game/Techtonica',
    winePrefix: '/home/death/techtonica-server/wine/prefix',
    bepinexLog: '/home/death/techtonica-server/game/Techtonica/BepInEx/LogOutput.log',
    debugLog: '/home/death/techtonica-server/debug.log',
    gameLog: '/home/death/techtonica-server/game/Techtonica/game.log',
    modConfig: '/home/death/techtonica-server/game/Techtonica/BepInEx/config/com.community.techtonicadedicatedserver.cfg',
    savesDir: '/home/death/techtonica-server/saves',
    usersFile: '/home/death/techtonica-server/admin-panel/users.json',
    port: 6969,
    basePath: '/techtonica-admin', // Base path for reverse proxy
    // SSL certificates (for HTTPS via certifriedmultitool.com)
    sslCert: '/etc/letsencrypt/live/certifriedmultitool.com/fullchain.pem',
    sslKey: '/etc/letsencrypt/live/certifriedmultitool.com/privkey.pem'
};

// Initialize users file if it doesn't exist
function initializeUsers() {
    if (!fs.existsSync(CONFIG.usersFile)) {
        const defaultAdmin = {
            id: crypto.randomUUID(),
            username: 'admin',
            password: bcrypt.hashSync('TechtonicaAdmin2024!', 10),
            role: 'admin',
            createdAt: new Date().toISOString(),
            lastLogin: null
        };
        const users = { users: [defaultAdmin] };
        fs.writeFileSync(CONFIG.usersFile, JSON.stringify(users, null, 2));
        console.log('Created default admin user (username: admin, password: TechtonicaAdmin2024!)');
    }
}

// Load users
function loadUsers() {
    try {
        const data = fs.readFileSync(CONFIG.usersFile, 'utf-8');
        return JSON.parse(data).users || [];
    } catch (err) {
        console.error('Error loading users:', err);
        return [];
    }
}

// Save users
function saveUsers(users) {
    try {
        fs.writeFileSync(CONFIG.usersFile, JSON.stringify({ users }, null, 2));
        return true;
    } catch (err) {
        console.error('Error saving users:', err);
        return false;
    }
}

// Initialize
initializeUsers();

// Session configuration
const sessionMiddleware = session({
    secret: crypto.randomBytes(32).toString('hex'),
    resave: false,
    saveUninitialized: false,
    cookie: {
        secure: false, // Set to true if using HTTPS
        httpOnly: true,
        maxAge: 24 * 60 * 60 * 1000 // 24 hours
    }
});

app.use(sessionMiddleware);
app.use(cors({
    origin: true,
    credentials: true
}));
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Strip basePath prefix from incoming requests (for reverse proxy)
app.use((req, res, next) => {
    if (req.path.startsWith(CONFIG.basePath)) {
        req.url = req.url.replace(CONFIG.basePath, '') || '/';
    }
    next();
});

// Authentication middleware
function requireAuth(req, res, next) {
    if (req.session && req.session.user) {
        return next();
    }
    if (req.xhr || req.path.startsWith('/api/')) {
        return res.status(401).json({ error: 'Unauthorized' });
    }
    return res.redirect(CONFIG.basePath + '/login');
}

function requireAdmin(req, res, next) {
    if (req.session && req.session.user && req.session.user.role === 'admin') {
        return next();
    }
    if (req.xhr || req.path.startsWith('/api/')) {
        return res.status(403).json({ error: 'Admin access required' });
    }
    return res.status(403).send('Admin access required');
}

// Static files (public routes)
app.use('/css', express.static(path.join(__dirname, 'public/css')));
app.use('/js', express.static(path.join(__dirname, 'public/js')));
app.use('/images', express.static(path.join(__dirname, 'public/images')));
app.use('/fonts', express.static(path.join(__dirname, 'public/fonts')));

// ═══════════════════════════════════════════════════════════════════
// AUTH ROUTES
// ═══════════════════════════════════════════════════════════════════

// Login page
app.get('/login', (req, res) => {
    if (req.session && req.session.user) {
        return res.redirect(CONFIG.basePath + '/');
    }
    res.sendFile(path.join(__dirname, 'public/login.html'));
});

// Login API
app.post('/api/auth/login', (req, res) => {
    const { username, password } = req.body;
    const users = loadUsers();
    const user = users.find(u => u.username === username);

    if (!user || !bcrypt.compareSync(password, user.password)) {
        return res.status(401).json({ error: 'Invalid username or password' });
    }

    // Update last login
    user.lastLogin = new Date().toISOString();
    saveUsers(users);

    // Set session
    req.session.user = {
        id: user.id,
        username: user.username,
        role: user.role
    };

    res.json({
        success: true,
        user: {
            username: user.username,
            role: user.role
        }
    });
});

// Logout
app.post('/api/auth/logout', (req, res) => {
    req.session.destroy();
    res.json({ success: true });
});

// Get current user
app.get('/api/auth/me', requireAuth, (req, res) => {
    res.json({ user: req.session.user });
});

// Change password
app.post('/api/auth/change-password', requireAuth, (req, res) => {
    const { currentPassword, newPassword } = req.body;
    const users = loadUsers();
    const user = users.find(u => u.id === req.session.user.id);

    if (!user || !bcrypt.compareSync(currentPassword, user.password)) {
        return res.status(400).json({ error: 'Current password is incorrect' });
    }

    user.password = bcrypt.hashSync(newPassword, 10);
    saveUsers(users);
    res.json({ success: true });
});

// ═══════════════════════════════════════════════════════════════════
// USER MANAGEMENT (Admin only)
// ═══════════════════════════════════════════════════════════════════

// Get all users
app.get('/api/users', requireAuth, requireAdmin, (req, res) => {
    const users = loadUsers().map(u => ({
        id: u.id,
        username: u.username,
        role: u.role,
        createdAt: u.createdAt,
        lastLogin: u.lastLogin
    }));
    res.json({ users });
});

// Create user
app.post('/api/users', requireAuth, requireAdmin, (req, res) => {
    const { username, password, role } = req.body;

    if (!username || !password) {
        return res.status(400).json({ error: 'Username and password required' });
    }

    const users = loadUsers();
    if (users.find(u => u.username === username)) {
        return res.status(400).json({ error: 'Username already exists' });
    }

    const newUser = {
        id: crypto.randomUUID(),
        username,
        password: bcrypt.hashSync(password, 10),
        role: role || 'manager',
        createdAt: new Date().toISOString(),
        lastLogin: null
    };

    users.push(newUser);
    saveUsers(users);

    res.json({
        success: true,
        user: {
            id: newUser.id,
            username: newUser.username,
            role: newUser.role
        }
    });
});

// Update user
app.put('/api/users/:id', requireAuth, requireAdmin, (req, res) => {
    const { id } = req.params;
    const { username, password, role } = req.body;
    const users = loadUsers();
    const user = users.find(u => u.id === id);

    if (!user) {
        return res.status(404).json({ error: 'User not found' });
    }

    if (username) user.username = username;
    if (password) user.password = bcrypt.hashSync(password, 10);
    if (role) user.role = role;

    saveUsers(users);
    res.json({ success: true });
});

// Delete user
app.delete('/api/users/:id', requireAuth, requireAdmin, (req, res) => {
    const { id } = req.params;
    let users = loadUsers();

    // Prevent deleting the last admin
    const admins = users.filter(u => u.role === 'admin');
    const userToDelete = users.find(u => u.id === id);

    if (userToDelete && userToDelete.role === 'admin' && admins.length <= 1) {
        return res.status(400).json({ error: 'Cannot delete the last admin user' });
    }

    users = users.filter(u => u.id !== id);
    saveUsers(users);
    res.json({ success: true });
});

// ═══════════════════════════════════════════════════════════════════
// PROTECTED ROUTES
// ═══════════════════════════════════════════════════════════════════

// Main dashboard (protected)
app.get('/', requireAuth, (req, res) => {
    res.sendFile(path.join(__dirname, 'public/index.html'));
});

// Server state
let serverProcess = null;
let xvfbProcess = null;
let serverStatus = 'stopped';
let lastLogPosition = 0;
let serverStartTime = null;

// Helper: Parse INI config
function parseConfig() {
    try {
        if (!fs.existsSync(CONFIG.modConfig)) {
            return {};
        }
        const content = fs.readFileSync(CONFIG.modConfig, 'utf-8');
        const config = {};
        let currentSection = '';
        content.split('\n').forEach(line => {
            line = line.trim();
            if (line.startsWith('[') && line.endsWith(']')) {
                currentSection = line.slice(1, -1);
                config[currentSection] = {};
            } else if (line && !line.startsWith('#') && !line.startsWith(';') && line.includes('=')) {
                const [key, ...valueParts] = line.split('=');
                const value = valueParts.join('=').trim();
                if (currentSection) {
                    config[currentSection][key.trim()] = value;
                }
            }
        });
        return config;
    } catch (err) {
        console.error('Error parsing config:', err);
        return {};
    }
}

// Helper: Save INI config
function saveConfig(config) {
    try {
        let content = `## Settings file was created by plugin Techtonica Dedicated Server v0.1.0
## Plugin GUID: com.community.techtonicadedicatedserver

`;
        for (const section in config) {
            content += `[${section}]\n\n`;
            for (const key in config[section]) {
                content += `${key} = ${config[section][key]}\n`;
            }
            content += '\n';
        }
        fs.writeFileSync(CONFIG.modConfig, content);
        return true;
    } catch (err) {
        console.error('Error saving config:', err);
        return false;
    }
}

// Helper: Get recent logs
function getRecentLogs(logFile, lines = 100) {
    try {
        if (!fs.existsSync(logFile)) {
            return ['No log file found'];
        }
        const content = fs.readFileSync(logFile, 'utf-8');
        const allLines = content.split('\n');
        return allLines.slice(-lines);
    } catch (err) {
        return [`Error reading logs: ${err.message}`];
    }
}

// Helper: Get save files
function getSaveFiles() {
    try {
        const saves = [];
        // Check local saves
        if (fs.existsSync(CONFIG.savesDir)) {
            fs.readdirSync(CONFIG.savesDir).forEach(file => {
                if (file.endsWith('.dat')) {
                    const stat = fs.statSync(path.join(CONFIG.savesDir, file));
                    saves.push({
                        name: file,
                        path: path.join(CONFIG.savesDir, file),
                        size: stat.size,
                        modified: stat.mtime
                    });
                }
            });
        }
        // Check Wine saves
        const wineSavesDir = path.join(CONFIG.winePrefix, 'drive_c/users/death/AppData/LocalLow/Fire Hose Games/Techtonica/saves');
        if (fs.existsSync(wineSavesDir)) {
            const walkSync = (dir, base = '') => {
                fs.readdirSync(dir).forEach(file => {
                    const fullPath = path.join(dir, file);
                    const relPath = base ? `${base}/${file}` : file;
                    if (fs.statSync(fullPath).isDirectory()) {
                        walkSync(fullPath, relPath);
                    } else if (file.endsWith('.dat')) {
                        const stat = fs.statSync(fullPath);
                        saves.push({
                            name: relPath,
                            path: fullPath,
                            size: stat.size,
                            modified: stat.mtime,
                            location: 'wine'
                        });
                    }
                });
            };
            walkSync(wineSavesDir);
        }
        return saves.sort((a, b) => new Date(b.modified) - new Date(a.modified));
    } catch (err) {
        console.error('Error getting saves:', err);
        return [];
    }
}

// Check if server is actually running and get process info
function checkServerRunning() {
    return new Promise((resolve) => {
        // Find Techtonica.exe process with -batchmode (the server)
        exec('ps -eo pid,etimes,args | grep "Techtonica.exe.*-batchmode" | grep -v grep | head -1', (err, stdout) => {
            const line = stdout.trim();
            if (!line) {
                resolve({ running: false, pid: null, uptime: 0 });
                return;
            }
            const parts = line.trim().split(/\s+/);
            const pid = parseInt(parts[0]) || null;
            const uptime = parseInt(parts[1]) || 0; // etimes = elapsed time in seconds
            resolve({ running: true, pid, uptime });
        });
    });
}

// ═══════════════════════════════════════════════════════════════════
// SERVER API ROUTES (Protected)
// ═══════════════════════════════════════════════════════════════════

// Get server status
app.get('/api/status', requireAuth, async (req, res) => {
    const config = parseConfig();
    const serverInfo = await checkServerRunning();

    if (serverInfo.running && serverStatus !== 'running') {
        serverStatus = 'running';
    } else if (!serverInfo.running && serverStatus === 'running') {
        serverStatus = 'stopped';
        serverProcess = null;
    }

    res.json({
        status: serverInfo.running ? 'running' : serverStatus,
        pid: serverInfo.pid || serverProcess?.pid || null,
        config: config,
        uptime: serverInfo.uptime || (serverStartTime ? Math.floor((Date.now() - serverStartTime) / 1000) : 0),
        serverAddress: '51.81.155.59:6968'
    });
});

// Get configuration
app.get('/api/config', requireAuth, (req, res) => {
    res.json(parseConfig());
});

// Update configuration
app.post('/api/config', requireAuth, (req, res) => {
    const success = saveConfig(req.body);
    res.json({ success });
});

// Get logs
app.get('/api/logs', requireAuth, (req, res) => {
    const lines = parseInt(req.query.lines) || 100;
    const logType = req.query.type || 'bepinex';

    let logFile;
    switch (logType) {
        case 'debug':
            logFile = CONFIG.debugLog;
            break;
        case 'game':
            logFile = CONFIG.gameLog;
            break;
        default:
            logFile = CONFIG.bepinexLog;
    }

    res.json({ logs: getRecentLogs(logFile, lines) });
});

// Get save files
app.get('/api/saves', requireAuth, (req, res) => {
    res.json({ saves: getSaveFiles() });
});

// Start server
app.post('/api/server/start', requireAuth, async (req, res) => {
    const serverInfo = await checkServerRunning();
    if (serverInfo.running) {
        serverStatus = 'running';
        return res.json({ success: false, message: 'Server is already running' });
    }

    const { savePath = '' } = req.body || {};

    try {
        serverStatus = 'starting';

        // Update config for auto-start
        const config = parseConfig();
        config['Server'] = config['Server'] || {};
        config['Server']['AutoStartServer'] = 'true';
        config['Server']['HeadlessMode'] = 'true';
        config['General'] = config['General'] || {};
        config['General']['EnableDirectConnect'] = 'true';

        if (savePath) {
            config['Server']['AutoLoadSave'] = savePath;
        }

        saveConfig(config);

        // Clear old logs
        [CONFIG.bepinexLog, CONFIG.debugLog].forEach(log => {
            if (fs.existsSync(log)) {
                fs.writeFileSync(log, '');
            }
        });
        lastLogPosition = 0;

        // Check if Xvfb is running on :98
        exec('pgrep -f "Xvfb :98"', (err, stdout) => {
            if (!stdout.trim()) {
                // Start Xvfb
                xvfbProcess = spawn('Xvfb', [':98', '-screen', '0', '1024x768x24'], {
                    detached: true,
                    stdio: 'ignore'
                });
                xvfbProcess.unref();
            }
        });

        await new Promise(resolve => setTimeout(resolve, 2000));

        // Start game with Wine
        const env = {
            ...process.env,
            DISPLAY: ':98',
            WINEPREFIX: CONFIG.winePrefix,
            WINEDLLOVERRIDES: 'winhttp=n,b',
            WINEDEBUG: '-all'
        };

        serverProcess = spawn('wine', ['Techtonica.exe'], {
            cwd: CONFIG.gameDir,
            env: env,
            detached: true,
            stdio: ['ignore', 'pipe', 'pipe']
        });

        serverProcess.unref();
        serverStartTime = Date.now();

        serverProcess.on('close', (code) => {
            serverStatus = 'stopped';
            serverProcess = null;
            serverStartTime = null;
        });

        serverStatus = 'running';
        res.json({
            success: true,
            pid: serverProcess.pid,
            message: 'Server starting...'
        });

    } catch (err) {
        serverStatus = 'error';
        res.json({ success: false, message: err.message });
    }
});

// Stop server
app.post('/api/server/stop', requireAuth, async (req, res) => {
    try {
        exec('pkill -f "wine.*Techtonica"');
        exec('pkill -f wineserver');

        serverProcess = null;
        serverStatus = 'stopped';
        serverStartTime = null;

        res.json({ success: true });
    } catch (err) {
        res.json({ success: false, message: err.message });
    }
});

// Restart server
app.post('/api/server/restart', requireAuth, async (req, res) => {
    try {
        // Stop
        exec('pkill -f "wine.*Techtonica"');
        exec('pkill -f wineserver');
        serverProcess = null;
        serverStatus = 'stopped';

        await new Promise(resolve => setTimeout(resolve, 5000));

        // Start (redirect to start endpoint)
        res.redirect(307, '/api/server/start');
    } catch (err) {
        res.json({ success: false, message: err.message });
    }
});

// Upload save file
app.post('/api/saves/upload', requireAuth, (req, res) => {
    // This would require multer for file uploads
    res.json({ success: false, message: 'File upload not yet implemented' });
});

// ═══════════════════════════════════════════════════════════════════
// SOCKET.IO SETUP
// ═══════════════════════════════════════════════════════════════════

// Try HTTPS first, fall back to HTTP
let server;
try {
    if (fs.existsSync(CONFIG.sslCert) && fs.existsSync(CONFIG.sslKey)) {
        const sslOptions = {
            cert: fs.readFileSync(CONFIG.sslCert),
            key: fs.readFileSync(CONFIG.sslKey)
        };
        server = https.createServer(sslOptions, app);
        console.log('HTTPS enabled');
    } else {
        server = http.createServer(app);
        console.log('Running in HTTP mode (no SSL certificates found)');
    }
} catch (err) {
    server = http.createServer(app);
    console.log('Running in HTTP mode:', err.message);
}

const io = new Server(server, {
    cors: { origin: '*', credentials: true }
});

// Share session with Socket.IO
io.use((socket, next) => {
    sessionMiddleware(socket.request, {}, next);
});

io.on('connection', (socket) => {
    const user = socket.request.session?.user;
    if (!user) {
        socket.disconnect();
        return;
    }

    console.log(`Client connected: ${user.username}`);
    socket.emit('status', { status: serverStatus, pid: serverProcess?.pid });

    // Send log updates periodically
    const logInterval = setInterval(() => {
        if (fs.existsSync(CONFIG.bepinexLog)) {
            try {
                const stat = fs.statSync(CONFIG.bepinexLog);
                if (stat.size > lastLogPosition) {
                    const stream = fs.createReadStream(CONFIG.bepinexLog, {
                        start: lastLogPosition,
                        encoding: 'utf-8'
                    });
                    let newLogs = '';
                    stream.on('data', chunk => newLogs += chunk);
                    stream.on('end', () => {
                        if (newLogs) {
                            socket.emit('log', { data: newLogs });
                        }
                        lastLogPosition = stat.size;
                    });
                }
            } catch (err) {
                // Ignore read errors
            }
        }
    }, 2000);

    socket.on('disconnect', () => {
        console.log(`Client disconnected: ${user.username}`);
        clearInterval(logInterval);
    });
});

// Start the server
server.listen(CONFIG.port, '0.0.0.0', () => {
    const protocol = server instanceof https.Server ? 'https' : 'http';
    console.log(`Techtonica Server Admin Panel running on ${protocol}://0.0.0.0:${CONFIG.port}`);
    console.log(`Access via: https://certifriedmultitool.com:${CONFIG.port}`);
    console.log(`Or: http://51.81.155.59:${CONFIG.port}`);
});
