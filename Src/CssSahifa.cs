using System;
using RT.Util.ExtensionMethods;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RT.DocGen
{
    public static partial class Css
    {
        public const string Sahifa = @"body {
    font-family: ""Minion Pro Caption"", ""Georgia"", serif;
    font-size: 13pt;
    margin: 0;
    padding: 0;
}

.type, .Method, .Constructor, .Property, .Event, .Field, .Class, .Struct, .Enum, .Interface, .Delegate, .parameter, .generic-parameter, .namespace, .numeric, .assembly, code {
    font-family: ""Candara"", ""Segoe UI"", ""Verdana"", sans-serif;
    font-size: 11pt;
    color: #148;
}

h1 .type, h1 .Method, h1 .Constructor, h1 .Property, h1 .Event, h1 .Field, h1 .Class, h1 .Struct, h1 .Enum, h1 .Interface, h1 .Delegate, h1 .namespace, h1 .parameter {
    font-size: 20pt;
}

h1 .member-name {
    font-size: 24pt;
}

a {
    color: #147;
}

    a:visited {
        color: #1c69c0;
    }

td {
    vertical-align: top;
}

    td.left {
        padding-right: 1.5em;
        box-shadow: inset -4px 0px 9px -6px rgba(0,0,0,.3);
        text-align: center;
        min-width: 20em;
    }

        td.left > * {
            text-align: left;
        }

    td.right {
        width: 100%;
        padding: 3em 2em 10em;
    }

    td.numeric {
        text-align: right;
    }

.boxy {
    display: inline-block;
    margin: 1em 0;
    padding: .3em 1em;
}

.boxy.links, .auth {
    font-variant: small-caps;
    text-align: center;
}

.boxy.tree {
    display: block;
}

.tree ul {
    padding: 0;
}

.tree li {
    list-style-type: none;
    text-indent: -1em;
    padding-left: 1em;
}

.indent {
    margin-left: 1em;
}

.Class:before, .Struct:before, .Enum:before, .Interface:before, .Delegate:before,
.Constructor:before, .Method:before, .Property:before, .Event:before, .Field:before,
.tree li div.namespace:before {
    width: 20px;
    display: inline-block;
    text-align: center;
    margin-right: .5em;
    text-indent: 0;
    color: black;
    border: 1px solid black;
    border-left: 3px solid black;
}

div.Class:before { content: 'Cl'; border-color: rgb(226,133,5); color: rgb(226,133,5); }
div.Struct:before { content: 'St'; border-color: rgb(129,131,226); color: rgb(129,131,226); }
div.Enum:before { content: 'En'; border-color: rgb(234,88,81); color: rgb(234,88,81); }
div.Interface:before { content: 'In'; border-color: rgb(192,192,0); color: rgb(192,192,0); }
div.Delegate:before { content: 'De'; border-color: rgb(251,83,209); color: rgb(251,83,209); }
div.Constructor:before { content: 'C'; border-color: hsl(120, 100%, 80%); color: hsl(120, 100%, 65%); }
div.Method:before { content: 'M'; border-color: hsl(220, 100%, 90%); color: hsl(220, 100%, 80%); }
div.Property:before { content: 'P'; border-color: hsl(300, 100%, 90%); color: hsl(300, 100%, 80%); }
div.Event:before { content: 'E'; border-color: hsl(0, 100%, 90%); color: hsl(0, 100%, 80%); }
div.Field:before { content: 'F'; border-color: hsl(60, 75%, 70%); color: hsl(60, 75%, 50%); }

.tree li div.namespace:before {
    content: '{ }';
    font-family: ""Verdana"", sans-serif;
    font-weight: bold;
    font-size: 8pt;
    border: none;
}

.tree li div.assembly {
    margin-top: .7em;
    font-weight: bold;
    font-size: 16pt;
}

.tree li div.assembly:before {
    font-family: ""Tahoma"", sans-serif;
    font-weight: bold;
    font-size: 7pt;
    border: none;
}

.tree li span.type {
    font-weight: bold;
}

table.legend {
    min-width: 230px;
}

.legend p {
    text-align: center;
    margin: 0 -.7em;
    background: #f0f8ff;
    border: 1px solid #80a0ff;
    border-left-width: 4px;
}

td.legend {
    padding: .3em .5em;
}

.content {
    padding: 0;
    width: 100%;
}

.innercontent {
    padding: 0;
}

h1 {
    margin: 0 0 1em 0;
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
        color: #aac;
        font-weight: normal;
    }

    h1.namespace-heading .namespace {
        color: #114488;
        font-weight: bold;
    }

h2 {
    font-weight: bold;
    font-size: 20pt;
    border-bottom: 1px solid #bcf;
    margin: 1.5em 0 .8em;
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
    font-size: 11pt;
}

    pre, pre .Method, pre .Constructor {
        color: black;
    }

.membertype {
    text-align: right;
}

table {
    border-collapse: collapse;
}

    table.doclist td {
        border-top: 1px solid #dfefff;
        padding: .5em 1em;
    }

    table.doclist tr:first-child td {
        border-top: none;
    }

    table.doclist td .withicon {
        text-indent: -3em;
        padding-left: 3em;
    }

    table.doclist td.type {
        font-weight: bold;
        padding-left: 3em;
        text-indent: -3em;
    }

    table.doclist td.membertype {
        padding-right: .5em;
        padding-left: 0;
    }

    table.doclist td.member {
        padding-left: 0;
    }

        table.doclist td.member > div > span > strong.member-name,  /* member names, e.g. fields, properties, events */
        table.doclist td.member > div > span > strong.member-name .type,    /* constructors */
        table.doclist td.member > div > span.type {     /* nested types */
            font-size: 14pt;
            font-weight: bold;
        }

    table.doclist td.documentation {
        border-left: 1px solid #dfefff;
    }

ul.extra {
    margin: .2em 0 0 2.6em;
    padding: 0;
    list-style-type: none;
}

    ul.extra li {
        margin: 0;
    }

.extra {
    font-size: 9pt;
    opacity: .4;
}

    .extra .type, .extra .Method, .extra .Constructor, .extra .Property, .extra .Event, .extra .Field, .extra .Class, .extra .Struct, .extra .Enum, .extra .Interface, .extra .Delegate, .extra .namespace, .extra .parameter, .extra .member-name {
        font-size: 8pt !important;
    }

.sep {
    padding: 0 1em;
}

b {
    padding-right: 1em;
}

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
