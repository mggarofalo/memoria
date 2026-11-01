/**
 * An error that maps directly to an HTTP response. The app's error handler
 * turns it into `{ error: code, message }` with the given status.
 */
export class HttpError extends Error {
  readonly statusCode: number;
  readonly code: string;
  constructor(statusCode: number, code: string, message: string) {
    super(message);
    this.name = "HttpError";
    this.statusCode = statusCode;
    this.code = code;
  }
}
