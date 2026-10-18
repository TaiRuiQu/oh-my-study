// Trusted fixed template. All untrusted text is read as JSON and passed to text().
#let data = json(sys.inputs.at("data"))
#let a3 = sys.inputs.at("paper") == "a3"
#let answers = data.kind == "answers"
#set document(title: data.title, author: "Oh My Study M0 probe")
#set page(paper: if a3 { "a3" } else { "a4" }, flipped: a3,
  margin: (x: 17mm, top: 18mm, bottom: 17mm),
  footer: context align(center, text(size: 9pt)[排版验证 · #counter(page).display("1 / 1", both: true)]))
#set text(font: "Source Han Serif SC", size: 11pt, lang: "zh", fill: black)
#set par(leading: 0.75em, justify: false, spacing: 0.65em)
#let segments(items) = {
  for item in items {
    if item.type == "text" { text(item.text) }
    else if item.type == "math" {
      box(baseline: item.depthPt * 1pt,
        image(item.path, width: item.widthPt * 1pt, height: item.heightPt * 1pt))
    }
  }
}
#let qstart(q) = block(above: 2mm, below: 1.5mm, sticky: true)[#strong[#q.number. ]#segments(q.body)]
#let content() = {
  for q in data.questions {
    if "section" in q { block(above: 5mm, below: 3mm, sticky: true, text(size: 12pt, weight: "bold", q.section)) }
    if answers {
      block(breakable: false)[
        #qstart(q)
        #block(text(q.answer))
        #block(text(q.explanation))
      ]
      if "parts" in q {
        for p in q.parts { block(breakable: false)[#text(str(q.number) + p.label) #text(p.answer)] }
      }
    } else {
      qstart(q)
      if "options" in q {
        block(breakable: false, grid(columns: (1fr, 1fr), gutter: 2mm, ..q.options.map(o => text(o))))
      }
      if "table" in q {
        block(breakable: false, table(columns: (1fr, 1fr, 2fr, 2fr), inset: 2mm, stroke: 0.4pt, ..q.table.flatten().map(c => text(c))))
      }
      if "figurePath" in q {
        block(breakable: false, align(center, image(q.figurePath, width: 70mm)))
      }
      if "parts" in q {
        for (i, p) in q.parts.enumerate() {
          block(breakable: false, above: 2mm)[
            #text(if i == 0 { str(q.number) + p.label } else { str(q.number) + "（续）" + p.label })
            #segments(p.body)
            #v(p.blankMm * 1mm)
          ]
        }
      }
      if "blankMm" in q { v(q.blankMm * 1mm) }
    }
  }
}
#align(center, text(size: 18pt, weight: "bold", data.title))
#align(center, text(size: 10pt, if answers { "答案解析册 · 自建样题" } else { "题目卷 · 姓名：____________  日期：____________" }))
#v(4mm)
#if a3 { columns(2, gutter: 12mm, content()) } else { content() }
