const fs=require('node:fs');
const path=require('node:path');
const {createRequire}=require('node:module');
const root=path.resolve(__dirname,'..');
const dependencyRoot=process.env.AIMMOD_COACHING_BUILD_TOOLS||path.join(root,'tools/coaching-build');
const load=createRequire(path.join(dependencyRoot,'package.json'));
const ts=load('typescript'),esbuild=load('esbuild');
esbuild.build({entryPoints:[path.join(root,'in-game/ui/coaching.ts')],outfile:path.join(root,'in-game/ui/coaching.js'),bundle:true,format:'iife',target:'es5',nodePaths:[path.join(dependencyRoot,'node_modules')],legalComments:'inline',minify:true,banner:{js:'/*! core-js license\n'+fs.readFileSync(load.resolve('core-js/LICENSE'),'utf8')+'\n*/'},plugins:[{name:'typescript-es5',setup(build){build.onLoad({filter:/\.tsx?$/},async args=>({contents:ts.transpileModule(fs.readFileSync(args.path,'utf8'),{compilerOptions:{target:ts.ScriptTarget.ES5,module:ts.ModuleKind.ESNext,downlevelIteration:true,importHelpers:false}}).outputText,loader:'js',resolveDir:path.dirname(args.path)}));}}]}).catch(error=>{console.error(error&&error.message||error);process.exitCode=1;});
