using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RT.DocGen
{
    public static partial class Css
    {
        public const string Sky = @"
body {
    background: #eef;
    font-family: ""Candara"", ""Segoe UI"", ""Verdana"", sans-serif;
    font-size: 11pt;
    margin-bottom: 10em;
}

td {
    vertical-align: top;
}
td.left {
    padding-right: 1.5em;
}
td.right {
    width: 100%;
}

.boxy {
    border: 2px solid #666;
    border-top-color: #ccc;
    border-left-color: #aaa;
    border-right-color: #666;
    border-bottom-color: #444;
    border-radius: 10px;
    background: white;
    box-shadow: 3px 3px 3px #ccc;
    margin-bottom: 1em;
    padding: .3em 1em;
}

.links, .auth {
    font-variant: small-caps;
    text-align: center;
}

.tree ul {
    padding: 0;
}

.tree li {
    list-style-type: none;
    text-indent: -1em;
    padding-left: 1em;
}

.tree li div.namespace:before {
    content: '{ }';
    font-family: ""Verdana"", sans-serif;
    font-weight: bold;
    font-size: 8pt;
}

.tree li div.assembly {
    margin-top: .7em;
    font-weight: bold;
    font-size: 16pt;
}

.Class:before, .Struct:before, .Enum:before, .Interface:before, .Delegate:before,
    .Constructor:before, .Method:before, .Property:before, .Event:before, .Field:before,
    .tree li div.namespace:before {
    width: 23px;
    display: inline-block;
    text-align: center;
    border-radius: 5px;
    margin-right: .5em;
    margin-top: 1px;
    text-indent: 0;
    padding-left: 0;
    color: black;
}

div.Class:before       { content: 'Cl'; background: #4df; }
div.Struct:before      { content: 'St'; background: #f9f; }
div.Enum:before        { content: 'En'; background: #4f8; }
div.Interface:before   { content: 'In'; background: #f44; }
div.Delegate:before    { content: 'De'; background: #ff4; }
div.Constructor:before { content: 'C'; background: #bfb; }
div.Method:before      { content: 'M'; background: #cdf; }
div.Property:before    { content: 'P'; background: #fcf; }
div.Event:before       { content: 'E'; background: #faa; }
div.Field:before       { content: 'F'; background: #ee8; }

table.legend { width: 100%; }

div.legend > p {
    font-variant: small-caps;
    text-align: center;
    margin: 0 -.7em;
    background: -moz-linear-gradient(#e8f0ff, #d0e0f8);
    background: linear-gradient(#e8f0ff, #d0e0f8);
    border-radius: 5px;
}

td.legend > div {
    white-space: nowrap;
}

.content {
    padding: 0;
}

.innercontent {
    padding: 1em 2em 3em;
}

h1 {
    border-top-left-radius: 8px;
    border-top-right-radius: 8px;
    background: -moz-linear-gradient(#fff, #abe);
    background: linear-gradient(#fff, #abe);
    margin: 0;
    padding: .5em 1em;
    font-weight: bold;
    font-size: 24pt;
    border-bottom: 1px solid #888;
}

h1 .Method, h1 .Constructor {
    font-weight: normal;
    color: #668;
}

h1 .Method strong, h1 .Constructor strong {
    font-weight: bold;
    color: black;
}

h1 .namespace {
    color: #668;
    font-weight: normal;
}

h2 {
    font-weight: bold;
    font-size: 18pt;
    border-bottom: 1px solid #aad;
    margin: 1.5em 0 .8em;
    background: -moz-linear-gradient(left, #dde 0%, #fff 15%);
    background: linear-gradient(left, #dde 0%, #fff 20%);
    border-top-left-radius: 10px;
    padding: 0 1em;
}

pre, code {
    font-family: ""Candara"", ""Segoe UI"", ""Verdana"", sans-serif;
}

pre {
    background: #eee;
    padding: .7em 1.5em;
    border-left: 3px solid #8ad;
    border-top-right-radius: 20px;
    border-bottom-right-radius: 20px;
}

code {
    color: #468;
    font-weight: bold;
    padding: 0 .2em;
}

.membertype { text-align: right; }

table.doclist {
    border-collapse: collapse;
}
table.doclist td {
    border: 1px solid #cde;
    padding: .5em 1em;
}
table.doclist td .withicon {
    text-indent: -3em;
    padding-left: 3em;
}

.extra {
    font-size: 8pt;
    opacity: .4;
}
ul.extra {
    margin: .2em 0 0 3em;
    padding: 0;
    list-style-type: none;
}
ul.extra li {
    margin: 0;
}

.sep { padding: 0 1em; }
b { padding-right: 1em; }

.missing {
    background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAYAAACNiR0NAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAC8SURBVDhPY2AYtiBBgX92vDz/zXh5vkMUexJkGNhAOf6sBAWB//FyfEYUGQpyWZaGADdVXBirwKUK8iYQ20Ncx59FmeuA3kuQF6gEuq6LKuEHCq9ERYFokGEgmiLXgTSDvAgylCquAxsI9CrIZSBvU+w6mIGgJEMVw0CGAGN2LdVcFy0rJAFKe1RzHSTtUdG7VHMZ1Q2C5gxgycJ/M06B35ciCyAlCv9scNqjRt4FuQiEQckFlKgpch05mgHc9Dht+XE3awAAAABJRU5ErkJggg==) no-repeat right center;
}

.highlighted {
    background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAYAAACNiR0NAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAACDSURBVDhPzdPNDYAgDIZhNnEU3MRRcBJX0UlcRduEJv4V2g8OkpAeSJ5weBvCn09M69DlfwRN47ztNFMTKBBhB18YfEIwqEFusAaZQStUBb2QCqLQC+QguSN5QOctm4wuKKZ22AIXw0Zg06Z4YBMoy26BXaAFhsAS3AR+wV3AK0xgPAFi473SDF1CWAAAAABJRU5ErkJggg==) no-repeat right center;
}

.warning {
    font-size: 14pt;
    margin: 2em 2em;
}
";
    }
}
