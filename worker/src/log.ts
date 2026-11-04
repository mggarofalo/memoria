/** JSON lines on stdout, matching the sidecar's shape closely enough to grep together. */

export interface Logger {
  info(fields: object, message: string): void;
  warn(fields: object, message: string): void;
  error(fields: object, message: string): void;
}

export function createLogger(secret: string | undefined, write: (line: string) => void = (line) => process.stdout.write(`${line}\n`)): Logger {
  const emit = (level: string, fields: object, message: string): void => {
    let line = JSON.stringify({ level, time: new Date().toISOString(), msg: message, ...fields });
    // Nothing should pass the token here. This is the backstop if something does.
    if (secret !== undefined) line = line.split(secret).join("[redacted]");
    write(line);
  };
  return {
    info: (fields, message) => emit("info", fields, message),
    warn: (fields, message) => emit("warn", fields, message),
    error: (fields, message) => emit("error", fields, message),
  };
}

export const silentLogger: Logger = { info: () => {}, warn: () => {}, error: () => {} };
