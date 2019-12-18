namespace STD.Parsers

open DomainAgnostic
open Rules
open Thoth.Json.Net
open System

module Traefik =

    type ParseOpts =
        { exitPort: int
          entryPoints: string Set
          fixKubeCRD: bool }

    type RawRoute =
        { name: string
          enabled: bool
          using: string Set
          rule: string
          tls: bool }

        static member Decoder =
            Decode.object (fun get ->
                { name = get.Required.Field "name" Decode.string
                  enabled = (get.Required.Field "status" Decode.string).ToLower() = "enabled"
                  using = get.Required.Field "using" (Decode.array Decode.string) |> Set
                  rule = get.Required.Field "rule" Decode.string
                  tls = (get.Optional.Field "tls" Decode.value).IsSome })

    type Route =
        { name: string
          location: string
          uris: string seq }

    type FailedRoute =
        { name: string
          location: string
          reason: string }

    let decode = Decode.array RawRoute.Decoder |> Decode.fromString

    let parse using json =
        result {
            let! candidates = decode json |> Result.mapError Exception
            let (good, bad) =
                candidates
                |> Seq.filter (fun c -> c.enabled && Set.intersect c.using using |> (Set.isEmpty >> not))
                |> Seq.fold (fun (good, bad) c ->
                    match proutes c.rule with
                    | Ok(g, b) ->
                        match Seq.isEmpty g with
                        | false -> (Seq.Appending (c, g) good, bad)
                        | true -> (good, Seq.Appending (c, "No routes discovered") bad)
                    | Error e -> (good, Seq.Appending (c, e.Message) bad)) (Seq.empty, Seq.empty)
            return good, bad
        }


    let locate (fullid: string) =
        let idx = fullid.LastIndexOf("@")
        fullid.Substring(0, idx), fullid.Substring(idx, fullid.Length - idx)


    let bin port (good: (RawRoute * Path seq) seq) (bad: (RawRoute * string) seq) =
        let succ =
            good
            |> Seq.map (fun (r, c) ->
                let (n, l) = locate r.name

                let protocol =
                    if r.tls then "https" else "http"

                let uris =
                    c
                    |> Seq.map (fun s -> sprintf "%s://%s:%d/%s" protocol s.host port (s.pathPrefix.TrimStart '/'))
                { name = n
                  location = l
                  uris = uris })

        let fail =
            bad
            |> Seq.map (fun (r, c) ->
                let (n, l) = locate r.name
                { name = n
                  location = l
                  reason = c })

        succ, fail

    let materialize (opts: ParseOpts) json =
        result {
            let! (good, bad) = parse opts.entryPoints json
            let (succ, fail) = bin opts.exitPort good bad
            return succ, fail
        }
