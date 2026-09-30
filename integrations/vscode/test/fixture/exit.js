// The program the integration tests run as a task and debug: node exit.js <code> [milliseconds]
// waits the milliseconds given (none by default), then exits with the code.
const code = Number(process.argv[2] ?? 0);
const wait = Number(process.argv[3] ?? 0);
setTimeout(() => process.exit(code), wait);
