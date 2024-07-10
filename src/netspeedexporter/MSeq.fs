module netspeedexporter.MSeq

let unwrapAsync (seq: Async<'a> seq) =
    async {
        let mutable results = []
        for seq in seq do
            let! nextResult = seq
            results <- results @ [ nextResult ]

        return results
    }
