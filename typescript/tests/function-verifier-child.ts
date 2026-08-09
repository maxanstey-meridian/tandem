import { assessImplementation } from "../sample/src/infrastructure/assess-implementation.js";

const sources = process.argv.slice(2);
const results = await Promise.all(sources.map(assessImplementation));
console.log(JSON.stringify(results));
process.exit(0);
