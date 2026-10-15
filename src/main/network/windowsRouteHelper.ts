import { spawn, type ChildProcess } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { app } from 'electron'

let child: ChildProcess | null = null
let ready: Promise<{ port: number; token: string }> | null = null

export function startWindowsRouteHelper(): Promise<{ port: number; token: string }> {
  if (process.platform !== 'win32') return Promise.reject(new Error('Windows only'))
  if (ready) return ready
  ready = new Promise((resolve, reject) => {
    const token = randomBytes(32).toString('hex')
    const project = join(app.getAppPath(), 'PlexoWinRoute', 'PlexoWinRoute.csproj')
    const executable = join(
      process.resourcesPath,
      'PlexoWinRoute',
      process.arch === 'arm64' ? 'win-arm64' : 'win-x64',
      'PlexoWinRoute.exe'
    )
    const command = app.isPackaged ? executable : 'dotnet'
    if (app.isPackaged && !existsSync(executable)) {
      reject(new Error('PlexoWinRoute executable was not packaged'))
      return
    }
    const args = app.isPackaged ? [token] : ['run', '--project', project, '--', token]
    const processHandle = spawn(command, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
    child = processHandle
    let settled = false
    let output = ''
    const timeout = setTimeout(() => finish(new Error('Windows route helper startup timed out')), 10000)
    const finish = (error?: Error, port?: number): void => {
      if (settled) return
      settled = true
      clearTimeout(timeout)
      if (error || !port) {
        processHandle.kill()
        ready = null
        reject(error ?? new Error('Invalid helper port'))
      } else resolve({ port, token })
    }
    processHandle.stdout?.on('data', (chunk: Buffer) => {
      output += chunk.toString('utf8')
      const match = /(?:^|\n)READY (\d+)(?:\r?\n|$)/.exec(output)
      if (match) finish(undefined, Number(match[1]))
      if (output.length > 8192) output = output.slice(-8192)
    })
    processHandle.on('error', (err) => finish(err))
    processHandle.on('exit', () => {
      if (!settled) finish(new Error('Windows route helper exited'))
      ready = null
      if (child === processHandle) child = null
    })
  })
  return ready
}

export function stopWindowsRouteHelper(): void {
  child?.kill()
  child = null
  ready = null
}
