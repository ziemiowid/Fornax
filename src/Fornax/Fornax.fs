module Fornax

open System
open System.IO
open Argu
open Suave
open Suave.Filters
open Suave.Operators

type FornaxExiter () =
    interface IExiter with
        member x.Name = "fornax exiter"
        member x.Exit (msg, errorCode) =
            if errorCode = ErrorCode.HelpText then
                printfn "%s" msg
                exit 0
            else
                printfn "Error with code %A received - exiting." errorCode
                printfn "%s" msg
                exit 1

type [<CliPrefixAttribute("")>] Arguments =
    | New
    | Build
    | Watch
    | Version
    | Clean
with
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | New -> "Create new web site"
            | Build -> "Build web site"
            | Watch -> "Start watch mode rebuilding "
            | Version -> "Print version"
            | Clean -> "Clean output and temp files"

let toArguments (result : ParseResults<Arguments>) =
    if result.Contains <@ New @> then Some New
    elif result.Contains <@ Build @> then Some Build
    elif result.Contains <@ Watch @> then Some Watch
    elif result.Contains <@ Version @> then Some Version
    elif result.Contains <@ Clean @> then Some Clean

    else None

let createFileWatcher dir handler =
    let fileSystemWatcher = new FileSystemWatcher()
    fileSystemWatcher.Path <- dir
    fileSystemWatcher.EnableRaisingEvents <- true
    fileSystemWatcher.IncludeSubdirectories <- true
    fileSystemWatcher.NotifyFilter <- NotifyFilters.DirectoryName ||| NotifyFilters.LastWrite ||| NotifyFilters.FileName
    fileSystemWatcher.Created.Add handler
    fileSystemWatcher.Changed.Add handler
    fileSystemWatcher.Deleted.Add handler
    fileSystemWatcher

let router basePath =
    choose [
        path "/" >=> Redirection.redirect "/index.html"
        (Files.browse (Path.Combine(basePath, "_public")))
    ]

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Arguments>(programName = "fornax", errorHandler = FornaxExiter ())

    if argv.Length = 0 then
        printfn "No arguments provided.  Try 'fornax help' for additional details."
        printfn "%s" <| parser.PrintUsage()
        1
    elif argv.Length > 1 then
        printfn "More than one argument was provided.  Please provide only a single argument.  Try 'fornax help' for additional details."
        printfn "%s" <| parser.PrintUsage()
        1
    else
        let result = parser.Parse argv |> toArguments
        let cwd = Directory.GetCurrentDirectory ()
        match result with
        | Some New ->
            // The path of the directory that holds the scaffolding for a new website.
            let newTemplateDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "newTemplate")

            // The path of Fornax.Core.dll, which is located where the dotnet tool is installed.
            let corePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fornax.Core.dll")

            // Copy the folders from the template directory into the current folder.
            Directory.GetDirectories(newTemplateDir, "*", SearchOption.AllDirectories)
            |> Seq.iter (fun p -> Directory.CreateDirectory(p.Replace(newTemplateDir, cwd)) |> ignore)

            // Copy the files from the template directory into the current folder.
            Directory.GetFiles(newTemplateDir, "*.*", SearchOption.AllDirectories)
            |> Seq.iter (fun p -> File.Copy(p, p.Replace(newTemplateDir, cwd)))

            // Create the _bin directory in the current folder.  It holds
            // Fornax.Core.dll, which is used to provide Intellisense/autocomplete
            // in the .fsx files.
            Directory.CreateDirectory(Path.Combine(cwd, "_bin")) |> ignore
            
            // Copy the Fornax.Core.dll into _bin
            File.Copy(corePath, "./_bin/Fornax.Core.dll")

            printfn "New project successfully created."

            0
        | Some Build ->
            try
                let results = Generator.generateFolder cwd
                0
            with
            | FornaxGeneratorException message ->
                Console.WriteLine message
                1
            | exn ->
                printfn "An unexpected error happend: %s%s%s" exn.Message Environment.NewLine exn.StackTrace
                1
        | Some Watch ->
            let mutable lastAccessed = Map.empty<string, DateTime>
            let waitingForChangesMessage = "Generated site with errors. Waiting for changes..."

            let guardedGenerate () =
                try
                    generateFolder cwd
                with
                | FornaxGeneratorException message ->
                    printfn "%s%s%s" message Environment.NewLine waitingForChangesMessage
                | exn ->
                    printfn "An unexpected error happend: %s%s%s" exn.Message Environment.NewLine exn.StackTrace
                    exit 1

            guardedGenerate ()

            use watcher = createFileWatcher cwd (fun e ->
                if not (e.FullPath.Contains "_public") && not (e.FullPath.Contains ".sass-cache") && not (e.FullPath.Contains ".git") && not (e.FullPath.Contains ".ionide") then
                    let lastTimeWrite = File.GetLastWriteTime(e.FullPath)
                    match lastAccessed.TryFind e.FullPath with
                    | Some lt when Math.Abs((lt - lastTimeWrite).Seconds) < 1 -> ()
                    | _ ->
                        printfn "[%s] Changes detected: %s" (DateTime.Now.ToString("HH:mm:ss")) e.FullPath
                        lastAccessed <- lastAccessed.Add(e.FullPath, lastTimeWrite)
                        guardedGenerate ())

            startWebServerAsync defaultConfig (router cwd) |> snd |> Async.Start
            printfn "[%s] Watch mode started. Press any key to exit." (DateTime.Now.ToString("HH:mm:ss"))
            Console.ReadKey() |> ignore
            printfn "Exiting..."
            0
        | Some Version ->
            printfn "%s" AssemblyVersionInformation.AssemblyVersion
            0
        | Some Clean ->
            let publ = Path.Combine(cwd, "_public")
            let sassCache = Path.Combine(cwd, ".sass-cache")
            try
                Directory.Delete(publ, true)
                Directory.Delete(sassCache, true)
                0
            with
            | _ -> 1
        | None ->
            printfn "Unknown argument"
            printfn "%s" <| parser.PrintUsage()
            1
