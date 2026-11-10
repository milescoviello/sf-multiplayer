#!/usr/bin/env python3
# Sets or removes the Stick Fight Online wrapper in Stick Fight's Steam launch
# options (userdata/<user>/config/localconfig.vdf, every Steam user on this
# machine). Steam rewrites that file when it exits, so Steam must be closed.
#   steam_options.py set   <steam-root> <wrapper>
#   steam_options.py unset <steam-root> <wrapper>
# Other launch options are kept: "%command%" becomes '"<wrapper>" %command%'
# (so WINEDLLOVERRIDES=... for other mods keeps working), options without
# %command% (plain game arguments) are kept after it. Each file is backed up
# once as localconfig.vdf.sf-online.bak.
import glob, os, shutil, sys

APPID = '674940'


def tokens(text):
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c.isspace():
            i += 1
        elif c == '/' and text.startswith('//', i):
            j = text.find('\n', i)
            i = n if j < 0 else j
        elif c in '{}':
            yield c
            i += 1
        elif c == '"':
            out, i = [], i + 1
            while i < n and text[i] != '"':
                if text[i] == '\\' and i + 1 < n:
                    out.append({'n': '\n', 't': '\t', '\\': '\\', '"': '"'}.get(text[i + 1], '\\' + text[i + 1]))
                    i += 2
                else:
                    out.append(text[i])
                    i += 1
            i += 1
            yield ('s', ''.join(out))
        else:  # unquoted token
            j = i
            while j < n and not text[j].isspace() and text[j] not in '{}"':
                j += 1
            yield ('s', text[i:j])
            i = j


def parse(text):
    # A node is a list of [key, value] pairs; value is a str or a node.
    root, stack, key = [], [], None
    cur = root
    for t in tokens(text):
        if t == '{':
            node = []
            cur.append([key, node])
            stack.append(cur)
            cur, key = node, None
        elif t == '}':
            cur = stack.pop()
        elif key is None:
            key = t[1]
        else:
            cur.append([key, t[1]])
            key = None
    if stack:
        raise ValueError('unbalanced braces')
    return root


def esc(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')


def dump(node, depth=0):
    out = []
    for k, v in node:
        tab = '\t' * depth
        if isinstance(v, list):
            out.append('%s"%s"\n%s{\n%s%s}\n' % (tab, esc(k), tab, dump(v, depth + 1), tab))
        else:
            out.append('%s"%s"\t\t"%s"\n' % (tab, esc(k), esc(v)))
    return ''.join(out)


def child(node, key, create):
    for kv in node:
        if kv[0].lower() == key.lower() and isinstance(kv[1], list):
            return kv[1]
    if not create:
        return None
    new = []
    node.append([key, new])
    return new


def app_node(root, create):
    n = root
    for k in ('UserLocalConfigStore', 'Software', 'Valve', 'Steam'):
        n = child(n, k, create)
        if n is None:
            return None
    apps = child(n, 'apps', create)
    return None if apps is None else child(apps, APPID, create)


def get_opt(app):
    for kv in app:
        if kv[0].lower() == 'launchoptions' and isinstance(kv[1], str):
            return kv[1]
    return ''


def set_opt(app, value):
    for kv in app:
        if kv[0].lower() == 'launchoptions' and isinstance(kv[1], str):
            kv[1] = value
            return
    app.append(['LaunchOptions', value])


def with_wrapper(old, wrapper):
    q = '"%s"' % wrapper
    if q in old:
        return old
    old = old.strip()
    if '%command%' in old:
        return old.replace('%command%', q + ' %command%', 1)
    return (q + ' %command% ' + old).strip()


def without_wrapper(old, wrapper):
    new = old.replace('"%s" ' % wrapper, '').replace('"%s"' % wrapper, '').strip()
    return '' if new == '%command%' else new


def main():
    if len(sys.argv) != 4 or sys.argv[1] not in ('set', 'unset'):
        print(__doc__ or 'usage: steam_options.py set|unset <steam-root> <wrapper>')
        return 2
    mode, root, wrapper = sys.argv[1:]
    files = glob.glob(os.path.join(root, 'userdata', '*', 'config', 'localconfig.vdf'))
    done = 0
    for path in files:
        try:
            with open(path, encoding='utf-8', errors='surrogateescape') as f:
                tree = parse(f.read())
            app = app_node(tree, mode == 'set')
            if app is None:
                continue
            old = get_opt(app)
            new = with_wrapper(old, wrapper) if mode == 'set' else without_wrapper(old, wrapper)
            if new == old:
                done += 1
                continue
            bak = path + '.sf-online.bak'
            if not os.path.exists(bak):
                shutil.copy2(path, bak)
            set_opt(app, new)
            tmp = path + '.sf-online.tmp'
            with open(tmp, 'w', encoding='utf-8', errors='surrogateescape') as f:
                f.write(dump(tree))
            os.replace(tmp, path)
            print('%s: launch options now: %s' % (path, new or '(none)'))
            done += 1
        except (OSError, ValueError) as e:
            print('%s: not changed (%s)' % (path, e))
    return 0 if done else 1


if __name__ == '__main__':
    sys.exit(main())
