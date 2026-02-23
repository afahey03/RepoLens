import * as vscode from 'vscode';

/**
 * Shared utility for creating styled webview panels.
 */
export function getWebviewContent(
    webview: vscode.Webview,
    title: string,
    bodyHtml: string,
    scriptJs: string = ''
): string {
    const nonce = getNonce();

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src ${webview.cspSource} 'nonce-${nonce}'; script-src 'nonce-${nonce}';" />
    <title>${title}</title>
    <style nonce="${nonce}">
        :root {
            --bg: var(--vscode-editor-background);
            --fg: var(--vscode-editor-foreground);
            --border: var(--vscode-panel-border);
            --link: var(--vscode-textLink-foreground);
            --badge: var(--vscode-badge-background);
            --badge-fg: var(--vscode-badge-foreground);
            --input-bg: var(--vscode-input-background);
            --input-fg: var(--vscode-input-foreground);
            --input-border: var(--vscode-input-border);
            --btn-bg: var(--vscode-button-background);
            --btn-fg: var(--vscode-button-foreground);
            --btn-hover: var(--vscode-button-hoverBackground);
        }
        * { box-sizing: border-box; }
        body {
            font-family: var(--vscode-font-family);
            font-size: var(--vscode-font-size);
            color: var(--fg);
            background: var(--bg);
            margin: 0;
            padding: 16px;
            line-height: 1.5;
        }
        h1, h2, h3 { margin: 0 0 12px; font-weight: 600; }
        h1 { font-size: 1.5em; }
        h2 { font-size: 1.25em; }
        h3 { font-size: 1.1em; }
        a { color: var(--link); text-decoration: none; }
        a:hover { text-decoration: underline; }
        .card {
            background: var(--vscode-sideBar-background, var(--bg));
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 14px 16px;
            margin-bottom: 14px;
        }
        .badge {
            display: inline-block;
            background: var(--badge);
            color: var(--badge-fg);
            border-radius: 10px;
            padding: 2px 10px;
            font-size: 0.85em;
            margin: 2px;
        }
        .bar {
            height: 10px;
            border-radius: 5px;
            display: inline-block;
            margin-right: 4px;
        }
        .bar-container {
            display: flex;
            border-radius: 5px;
            overflow: hidden;
            height: 14px;
            margin: 8px 0;
        }
        table { width: 100%; border-collapse: collapse; }
        th, td {
            text-align: left;
            padding: 6px 10px;
            border-bottom: 1px solid var(--border);
        }
        th { font-weight: 600; opacity: 0.8; font-size: 0.9em; }
        input[type="text"], input[type="search"] {
            width: 100%;
            padding: 6px 10px;
            border: 1px solid var(--input-border);
            background: var(--input-bg);
            color: var(--input-fg);
            border-radius: 4px;
            font-size: var(--vscode-font-size);
        }
        button {
            background: var(--btn-bg);
            color: var(--btn-fg);
            border: none;
            border-radius: 4px;
            padding: 6px 14px;
            cursor: pointer;
            font-size: var(--vscode-font-size);
        }
        button:hover { background: var(--btn-hover); }
        .muted { opacity: 0.65; }
        .grid { display: grid; gap: 12px; }
        .grid-2 { grid-template-columns: 1fr 1fr; }
        .grid-3 { grid-template-columns: 1fr 1fr 1fr; }
        .stat-value { font-size: 1.6em; font-weight: 700; }
        .stat-label { font-size: 0.85em; opacity: 0.7; }
        @media (max-width: 500px) {
            .grid-2, .grid-3 { grid-template-columns: 1fr; }
        }
    </style>
</head>
<body>
    ${bodyHtml}
    ${scriptJs ? `<script nonce="${nonce}">${scriptJs}</script>` : ''}
</body>
</html>`;
}

function getNonce(): string {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    let nonce = '';
    for (let i = 0; i < 32; i++) {
        nonce += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return nonce;
}
