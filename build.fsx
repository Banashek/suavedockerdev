#r "./packages/FSharp.Compiler.Service/lib/net45/FSharp.Compiler.Service.dll"
#r "./packages/Suave/lib/net40/Suave.dll"
#r "./packages/FAKE/tools/FakeLib.dll"
open Fake
open System
open System.Net
open System.IO
open System.Threading
open Suave
open Suave.Web
open Microsoft.FSharp.Compiler.Interactive.Shell

let sbOut = new Text.StringBuilder()
let sbErr = new Text.StringBuilder()

let fsiSession =
  let inStream = new StringReader("")
  let outStream = new StringWriter(sbOut)
  let errStream = new StringWriter(sbErr)
  let fsiConfig = FsiEvaluationSession.GetDefaultConfiguration()
  let argv = Array.append [|"/fake/fsi.exe"; "--quiet"; "--noninteractive"; "-d:DO_NOT_START_SERVER"|] [||]
  FsiEvaluationSession.Create(fsiConfig, argv, inStream, outStream, errStream)

let reportFsiError (e:exn) =
  traceError "Reloading app.fsx script failed."
  traceError (sprintf "Message: %s\nError: %s" e.Message (sbErr.ToString().Trim()))
  sbErr.Clear() |> ignore

let reloadScript () =
  try
    traceImportant "Reloading app.fsx script..."
    let appFsx = __SOURCE_DIRECTORY__ @@ "/sddweb/app.fsx"
    fsiSession.EvalInteraction(sprintf "#load @\"%s\"" appFsx)
    fsiSession.EvalInteraction("open App")
    match fsiSession.EvalExpression("app") with
    | Some app -> Some(app.ReflectionValue :?> WebPart)
    | None -> failwith "Couldn't get 'app' value"
  with e -> reportFsiError e; None

let currentApp = ref (fun _ -> async { return None })

let serverConfig =
  { defaultConfig with
      homeFolder = Some __SOURCE_DIRECTORY__
      logger = Logging.Loggers.saneDefaultsFor Logging.LogLevel.Debug
      bindings = [ HttpBinding.mk HTTP (IPAddress.Parse "0.0.0.0") 8083us] }

let reloadAppServer (changedFiles: string seq) =
  traceImportant <| sprintf "Changes in %s" (String.Join(",",changedFiles))
  reloadScript() |> Option.iter (fun app ->
    currentApp.Value <- app
    traceImportant "Refreshed app." )

Target "run" (fun _ ->
  let app ctx = currentApp.Value ctx
  let _, server = startWebServerAsync serverConfig app

  // Start Suave to host it on localhost
  reloadAppServer ["app.fsx"]
  Async.Start(server)

  // Watch for changes & reload when app.fsx changes
  let sources = 
    { BaseDirectory = __SOURCE_DIRECTORY__
      Includes = [ "**/*.fsx"; "**/*.fs" ; "**/*.fsproj"; "web/content/app/*.js" ]; 
      Excludes = [] }

  use watcher = sources |> WatchChanges (Seq.map (fun x -> x.FullPath) >> reloadAppServer)
  traceImportant "Waiting for app.fsx edits. Press any key to stop."
  // Hold thread open so that docker doesn't close the process when it detaches in compose
  Thread.Sleep(-1)
)

RunTargetOrDefault "run"
